using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Npgsql;
using NpgsqlTypes;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SmartLibrary.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// Chi lang nghe tren chinh may phat trien. WPF se goi backend qua HTTP loopback.
builder.WebHost.UseUrls("http://127.0.0.1:5082");

// Dang ky AuthService.cs cua buoc 29 voi Dependency Injection.
builder.Services.AddScoped<AuthService>();
// Quan trong: OTP tam luu tren RAM, phai dung Singleton giua /start va /confirm.
builder.Services.AddSingleton<RegistrationService>();
// Phien tam luu tren server, chi dung de test localhost.
builder.Services.AddSingleton<SessionService>();
// Developer-only one-time codes for Reader profile changes; no new file or database table.
builder.Services.AddSingleton<ReaderProfileEditOtpStore>();

var app = builder.Build();

// Explicit one-time CLI path. Never expose bootstrap as HTTP.
if (args.Contains("--bootstrap-admin", StringComparer.Ordinal))
{
    if (!app.Environment.IsDevelopment())
        throw new InvalidOperationException("Admin bootstrap only works in Development.");
    await AdminProvisioning.RunBootstrapAsync(app.Configuration);
    return;
}


// Giu nguyen endpoint kiem tra ket noi Aiven da chay duoc o buoc 28.
app.MapGet("/api/health", async (
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    string? connectionString = configuration.GetConnectionString("AivenPostgres");

    if (string.IsNullOrWhiteSpace(connectionString))
    {
        return Results.Json(new
        {
            status = "error",
            service = "SmartLibrary.Api",
            database = "not_configured"
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    try
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            "SELECT COUNT(*) FROM public.books;", connection);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        long bookCount = Convert.ToInt64(result);

        return Results.Ok(new
        {
            status = "ok",
            service = "SmartLibrary.Api",
            database = "connected",
            books = bookCount
        });
    }
    catch (Exception exception)
    {
        app.Logger.LogWarning(
            "PostgreSQL health check failed: {ErrorType}",
            exception.GetType().Name);

        return Results.Json(new
        {
            status = "error",
            service = "SmartLibrary.Api",
            database = "connection_failed",
            errorType = exception.GetType().Name
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

// Xac thuc username/password va cap session token RAM chi trong Development.
app.MapPost("/api/auth/login", async (
    LoginRequest request,
    AuthService authService,
    SessionService sessions,
    CancellationToken cancellationToken) =>
{
    try
    {
        if (!app.Environment.IsDevelopment())
            return Results.StatusCode(StatusCodes.Status404NotFound);
        var user = await authService.LoginAsync(
            request.Username,
            request.Password,
            cancellationToken);

        if (user is null)
        {
            // Tra thong bao chung, khong tiet lo username co ton tai hay khong.
            return Results.Json(new
            {
                message = "Sai thong tin dang nhap hoac tai khoan chua duoc kich hoat."
            }, statusCode: StatusCodes.Status401Unauthorized);
        }

        CreatedSession issued = sessions.Create(user.UserId, user.Username, user.Role);
        return Results.Ok(new
        {
            message = "credentials_valid",
            token = issued.Token,
            expiresAt = issued.ExpiresAt,
            user = new
            {
                userId = user.UserId,
                username = user.Username,
                role = user.Role
            }
        });
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
    }
    catch (Exception exception)
    {
        app.Logger.LogError(
            "Login API failed: {ErrorType}",
            exception.GetType().Name);

        return Results.Json(new
        {
            message = "Khong the xu ly dang nhap luc nay."
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

// Xac thuc session tu header Authorization: Bearer <token>.
// Khong nhan userId/role tu request client.
app.MapGet("/api/auth/me", (HttpRequest request, SessionService sessions) =>
{
    if (!app.Environment.IsDevelopment()) return Results.NotFound();
    var current = sessions.Validate(ReadBearerToken(request));
    if (current is null) return Results.Unauthorized();
    return Results.Ok(new
    {
        userId = current.UserId,
        username = current.Username,
        role = current.Role,
        expiresAt = current.ExpiresAt
    });
});

app.MapPost("/api/auth/logout", (HttpRequest request, SessionService sessions) =>
{
    if (!app.Environment.IsDevelopment()) return Results.NotFound();
    string? token = ReadBearerToken(request);
    if (sessions.Validate(token) is null) return Results.Unauthorized();
    sessions.Revoke(token);
    return Results.Ok(new { message = "logged_out" });
});

static string? ReadBearerToken(HttpRequest request)
{
    string raw = request.Headers.Authorization.ToString();
    const string prefix = "Bearer ";
    if (!raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
    string token = raw.Substring(prefix.Length).Trim();
    return token.Length is > 0 and <= 200 ? token : null;
}

// Dashboard read-only: verify the Bearer token on the server before querying DB.
// This is a local Development API, not a production authorization system.
app.MapGet("/api/dashboard/summary", async (
    HttpRequest request,
    SessionService sessions,
    IConfiguration config,
    CancellationToken cancellationToken) =>
{
    if (!app.Environment.IsDevelopment()) return Results.NotFound();
    var current = sessions.Validate(ReadBearerToken(request));
    if (current is null) return Results.Unauthorized();
    if (current.Role != "Reader" && current.Role != "Staff" && current.Role != "Admin")
        return Results.StatusCode(StatusCodes.Status403Forbidden);

    try
    {
        string connectionString = config.GetConnectionString("AivenPostgres")
            ?? throw new InvalidOperationException("Database is not configured.");
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        // Shared stats only. No finance/salary, personal data, or privileged actions.
        const string sql = @"
            SELECT
              (SELECT COUNT(*) FROM public.books),
              (SELECT COUNT(*) FROM public.authors),
              (SELECT COUNT(*) FROM public.categories),
              (SELECT COUNT(*) FROM public.book_copies),
              (SELECT COUNT(*) FROM public.reader_profiles);";
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        return Results.Ok(new
        {
            books = reader.GetInt64(0),
            authors = reader.GetInt64(1),
            categories = reader.GetInt64(2),
            physicalCopies = reader.GetInt64(3),
            readers = reader.GetInt64(4)
        });
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
    }
    catch (Exception error)
    {
        app.Logger.LogError("Dashboard summary failed: {ErrorType}", error.GetType().Name);
        return Results.Json(new { message = "Unable to load dashboard." },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

// Dang ky Reader chi de KIEM THU LOCAL. Khong hoat dong ngoai Development.
// OTP duoc in ra terminal API, khong gui SMS, chua xac minh so dien thoai that.
if (app.Environment.IsDevelopment())
{
    app.MapPost("/api/auth/register/start", async (
        RegisterStartRequest request,
        RegistrationService registrationService,
        CancellationToken cancellationToken) =>
    {
        try
        {
            StartRegistrationResult started = await registrationService.StartAsync(
                request.FullName,
                request.Username,
                request.Phone,
                request.Password,
                cancellationToken);

            return Results.Ok(new
            {
                message = "dev_otp_created",
                requestId = started.RequestId,
                expiresAt = started.ExpiresAt,
                notice = "Chi test local: xem ma OTP trong cua so API, chua gui SMS."
            });
        }
        catch (RegistrationException exception)
        {
            int httpStatus = exception.Code switch
            {
                "account_exists" => StatusCodes.Status409Conflict,
                "try_later" => StatusCodes.Status429TooManyRequests,
                _ => StatusCodes.Status400BadRequest
            };

            return Results.Json(new
            {
                code = exception.Code,
                message = exception.Message
            }, statusCode: httpStatus);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        catch (Exception exception)
        {
            app.Logger.LogError(
                "Register start failed: {ErrorType}",
                exception.GetType().Name);

            return Results.Json(new
            {
                message = "Khong the tao yeu cau dang ky luc nay."
            }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    });

    app.MapPost("/api/auth/register/confirm", async (
        RegisterConfirmRequest request,
        RegistrationService registrationService,
        CancellationToken cancellationToken) =>
    {
        try
        {
            CompleteRegistrationResult registered = await registrationService.ConfirmAsync(
                request.RequestId,
                request.Otp,
                cancellationToken);

            return Results.Ok(new
            {
                message = "registration_completed_dev_only",
                userId = registered.UserId,
                username = registered.Username,
                role = registered.Role
            });
        }
        catch (RegistrationException exception)
        {
            int httpStatus = exception.Code == "account_exists"
                ? StatusCodes.Status409Conflict
                : StatusCodes.Status400BadRequest;

            return Results.Json(new
            {
                code = exception.Code,
                message = exception.Message
            }, statusCode: httpStatus);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        catch (Exception exception)
        {
            app.Logger.LogError(
                "Register confirmation failed: {ErrorType}",
                exception.GetType().Name);

            return Results.Json(new
            {
                message = "Khong the hoan tat dang ky luc nay."
            }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    });
}


// Danh muc sach: chi doc, co kiem tra session tai backend.
app.MapGet("/api/books", async (
    HttpRequest request, SessionService sessions, IConfiguration config,
    string? q, CancellationToken cancellationToken) =>
{
    if (!app.Environment.IsDevelopment()) return Results.NotFound();
    var session = sessions.Validate(ReadBearerToken(request));
    if (session is null) return Results.Unauthorized();
    if (session.Role != "Reader" && session.Role != "Staff" && session.Role != "Admin")
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    q = q?.Trim() ?? "";
    if (q.Length > 100) return Results.BadRequest(new { message = "Tu khoa qua dai (toi da 100 ky tu)." });
    try
    {
        var cs = config.GetConnectionString("AivenPostgres")
            ?? throw new InvalidOperationException("Database not configured.");
        await using var db = new NpgsqlConnection(cs);
        await db.OpenAsync(cancellationToken);
        const string sql = @"
            SELECT b.book_id, b.title, COALESCE(b.isbn,''),
                   COALESCE(p.name,''), COALESCE(b.publication_year,0),
                   COALESCE((SELECT string_agg(a.name, ', ' ORDER BY a.name)
                             FROM public.book_authors ba
                             JOIN public.authors a ON a.author_id=ba.author_id
                             WHERE ba.book_id=b.book_id),''),
                   COALESCE((SELECT string_agg(c.name, ', ' ORDER BY c.name)
                             FROM public.book_categories bc
                             JOIN public.categories c ON c.category_id=bc.category_id
                             WHERE bc.book_id=b.book_id),''),
                   COALESCE((SELECT COUNT(*) FROM public.book_copies cp
                             WHERE cp.book_id=b.book_id),0)
            FROM public.books b
            LEFT JOIN public.publishers p ON p.publisher_id=b.publisher_id
            WHERE b.is_visible=TRUE
              AND (@query='' OR b.title ILIKE '%'||@query||'%'
                   OR b.isbn ILIKE '%'||@query||'%'
                   OR EXISTS (SELECT 1 FROM public.book_authors ba
                              JOIN public.authors a ON a.author_id=ba.author_id
                              WHERE ba.book_id=b.book_id AND a.name ILIKE '%'||@query||'%'))
            ORDER BY b.title, b.book_id LIMIT 100;";
        await using var cmd = new NpgsqlCommand(sql, db);
        cmd.Parameters.AddWithValue("query", q);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var books = new List<object>();
        while (await reader.ReadAsync(cancellationToken))
        {
            books.Add(new
            {
                bookId = reader.GetInt64(0),
                title = reader.GetString(1),
                isbn = reader.GetString(2),
                publisher = reader.GetString(3),
                publicationYear = reader.GetInt32(4),
                authors = reader.GetString(5),
                categories = reader.GetString(6),
                physicalCopies = reader.GetInt64(7)
            });
        }
        return Results.Ok(new { books });
    }
    catch (Exception e)
    {
        app.Logger.LogError("Books query failed: {ErrorType}", e.GetType().Name);
        return Results.Json(new { message = "Khong tai duoc danh muc sach." }, statusCode: 503);
    }
});


// Step 44: book detail is readable by logged-in roles. Copy changes require Staff/Admin.
// STEP 51: Full book details for a centred WPF detail dialog. Counts are derived
// from existing physical copies and active borrow details, not book metadata.
app.MapGet("/api/books/{bookId:long}/details", async (
    long bookId, HttpRequest request, SessionService sessions,
    IConfiguration config, CancellationToken ct) =>
{
    if (!app.Environment.IsDevelopment()) return Results.NotFound();
    var session = sessions.Validate(ReadBearerToken(request));
    if (session is null) return Results.Unauthorized();
    if (session.Role is not ("Reader" or "Staff" or "Admin")) return Results.StatusCode(403);
    try
    {
        string cs = config.GetConnectionString("AivenPostgres")
            ?? throw new InvalidOperationException("Database not configured.");
        await using var db = new NpgsqlConnection(cs);
        await db.OpenAsync(ct);
        const string sql = @"
            SELECT b.book_id, b.title, COALESCE(b.isbn,''),
                   COALESCE(b.description,''), COALESCE(p.name,''),
                   COALESCE(b.publication_year,0),
                   COALESCE((SELECT string_agg(a.name, ', ' ORDER BY a.name)
                     FROM public.book_authors ba JOIN public.authors a ON ba.author_id=a.author_id
                     WHERE ba.book_id=b.book_id),''),
                   COALESCE((SELECT string_agg(c.name, ', ' ORDER BY c.name)
                     FROM public.book_categories bc JOIN public.categories c ON bc.category_id=c.category_id
                     WHERE bc.book_id=b.book_id),''),
                   (SELECT COUNT(*) FROM public.book_copies c WHERE c.book_id=b.book_id),
                   (SELECT COUNT(*) FROM public.book_copies c
                     WHERE c.book_id=b.book_id AND c.condition_status='active'),
                   (SELECT COUNT(*) FROM public.book_copies c
                     WHERE c.book_id=b.book_id AND c.condition_status='active'
                       AND NOT EXISTS (
                         SELECT 1 FROM public.borrow_details bd
                         WHERE bd.book_copy_id=c.copy_id AND bd.closed_at IS NULL)),
                   (SELECT COUNT(*) FROM public.book_copies c
                     WHERE c.book_id=b.book_id AND EXISTS (
                         SELECT 1 FROM public.borrow_details bd
                         WHERE bd.book_copy_id=c.copy_id AND bd.closed_at IS NULL)),
                   COALESCE(b.cover_object_key,'')
            FROM public.books b
            LEFT JOIN public.publishers p ON p.publisher_id=b.publisher_id
            WHERE b.book_id=@id AND b.is_visible=TRUE;";
        await using var cmd = new NpgsqlCommand(sql, db);
        cmd.Parameters.AddWithValue("id", bookId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return Results.NotFound();
        return Results.Ok(new
        {
            bookId = reader.GetInt64(0),
            title = reader.GetString(1),
            isbn = reader.GetString(2),
            description = reader.GetString(3),
            publisher = reader.GetString(4),
            publicationYear = reader.GetInt32(5),
            authors = reader.GetString(6),
            categories = reader.GetString(7),
            totalCopies = reader.GetInt64(8),
            activeCopies = reader.GetInt64(9),
            availableCopies = reader.GetInt64(10),
            onLoanCopies = reader.GetInt64(11),
            coverUrl = BuildPublicCoverUrl(reader.GetString(12), config)
        });
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
        return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
    }
    catch (Exception error)
    {
        app.Logger.LogError("Book details failed: {Type}", error.GetType().Name);
        return Results.Json(new { message = "Khong tai duoc chi tiet sach." }, statusCode: 503);
    }
});

// Only expose cover images if there is an explicitly configured public HTTPS CDN.
// cover_object_key is a storage key, NEVER a URL or a database secret.
static string? BuildPublicCoverUrl(string key, IConfiguration config)
{
    string? baseUrl = config["BookCovers:PublicBaseUrl"]?.Trim();
    if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(key)) return null;
    if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var origin) || origin.Scheme != Uri.UriSchemeHttps)
        return null;
    if (key.StartsWith('/') || key.Contains('\\') || key.Contains('?') || key.Contains('#'))
        return null;
    string[] segments = key.Split('/');
    if (segments.Any(part => part.Length == 0 || part == "." || part == ".." || part.Contains(':')))
        return null;
    string path = string.Join("/", segments.Select(Uri.EscapeDataString));
    return baseUrl.TrimEnd('/') + "/" + path;
}

app.MapGet("/api/books/{bookId:long}/copies", async (
    long bookId, HttpRequest request, SessionService sessions,
    IConfiguration config, CancellationToken ct) =>
{
    if (!app.Environment.IsDevelopment()) return Results.NotFound();
    var session = sessions.Validate(ReadBearerToken(request));
    if (session is null) return Results.Unauthorized();
    if (session.Role is not ("Staff" or "Admin")) return Results.StatusCode(403);
    try
    {
        await using var db = new NpgsqlConnection(config.GetConnectionString("AivenPostgres"));
        await db.OpenAsync(ct);
        const string sql = @"SELECT copy_id, copy_code, condition_status, COALESCE(shelf_location,'')
                             FROM public.book_copies WHERE book_id=@id ORDER BY copy_code LIMIT 300;";
        await using var cmd = new NpgsqlCommand(sql, db);
        cmd.Parameters.AddWithValue("id", bookId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<object>();
        while (await r.ReadAsync(ct))
            rows.Add(new
            {
                copyId = r.GetInt64(0),
                copyCode = r.GetString(1),
                conditionStatus = r.GetString(2),
                shelfLocation = r.GetString(3)
            });
        return Results.Ok(new { copies = rows });
    }
    catch (Exception e)
    {
        app.Logger.LogError("Copy list failed: {Type}", e.GetType().Name);
        return Results.Json(new { message = "Khong tai duoc ban sao." }, statusCode: 503);
    }
});

app.MapPost("/api/books/{bookId:long}/copies", async (
    long bookId, AddCopyRequest body, HttpRequest request, SessionService sessions,
    IConfiguration config, CancellationToken ct) =>
{
    if (!app.Environment.IsDevelopment()) return Results.NotFound();
    var session = sessions.Validate(ReadBearerToken(request));
    if (session is null) return Results.Unauthorized();
    if (session.Role is not ("Staff" or "Admin")) return Results.StatusCode(403);
    var code = body.CopyCode?.Trim();
    var shelf = body.ShelfLocation?.Trim();
    if (string.IsNullOrWhiteSpace(code) || code.Length > 60 ||
        (shelf?.Length ?? 0) > 120)
        return Results.BadRequest(new { message = "Ma cuon 1-60 ky tu; ke sach toi da 120 ky tu." });
    try
    {
        await using var db = new NpgsqlConnection(config.GetConnectionString("AivenPostgres"));
        await db.OpenAsync(ct);
        const string sql = @"
            INSERT INTO public.book_copies(book_id,copy_code,condition_status,shelf_location)
            SELECT book_id, @code, 'active', @shelf FROM public.books
            WHERE book_id=@id AND is_visible=TRUE
            RETURNING copy_id;";
        await using var cmd = new NpgsqlCommand(sql, db);
        cmd.Parameters.AddWithValue("id", bookId);
        cmd.Parameters.AddWithValue("code", code);
        cmd.Parameters.AddWithValue("shelf", (object?)shelf ?? DBNull.Value);
        var id = await cmd.ExecuteScalarAsync(ct);
        if (id is null) return Results.NotFound();
        return Results.Created($"/api/books/{bookId}/copies", new { copyId = Convert.ToInt64(id), copyCode = code });
    }
    catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
    {
        return Results.Conflict(new { message = "Ma cuon da ton tai." });
    }
    catch (Exception e)
    {
        app.Logger.LogError("Copy create failed: {Type}", e.GetType().Name);
        return Results.Json(new { message = "Khong the tao ban sao." }, statusCode: 503);
    }
});


// Metadata directory endpoints — role enforced server-side (development only).
app.MapGet("/api/catalog/reference", async (HttpRequest req, SessionService sessions,
    IConfiguration config, CancellationToken ct) =>
{
    if (!app.Environment.IsDevelopment()) return Results.NotFound();
    var who = sessions.Validate(ReadBearerToken(req));
    if (who is null) return Results.Unauthorized();
    if (who.Role is not ("Reader" or "Staff" or "Admin")) return Results.StatusCode(403);
    try
    {
        await using var db = new NpgsqlConnection(config.GetConnectionString("AivenPostgres"));
        await db.OpenAsync(ct);
        async Task<List<object>> ReadAsync(string sql)
        {
            var list = new List<object>();
            await using var cmd = new NpgsqlCommand(sql, db);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                list.Add(new
                {
                    id = reader.GetInt64(0),
                    name = reader.GetString(1),
                    detail = reader.IsDBNull(2) ? "" : reader.GetString(2)
                });
            return list;
        }
        var authors = await ReadAsync("SELECT author_id,name,biography FROM public.authors ORDER BY name LIMIT 500");
        var publishers = await ReadAsync("SELECT publisher_id,name,contact_info FROM public.publishers ORDER BY name LIMIT 500");
        var categories = await ReadAsync("SELECT category_id,name,CAST(parent_id AS TEXT) FROM public.categories ORDER BY name LIMIT 500");
        return Results.Ok(new { authors, publishers, categories });
    }
    catch (Exception ex)
    {
        app.Logger.LogError("Catalog reference failed: {Type}", ex.GetType().Name);
        return Results.Json(new { message = "Khong tai duoc danh muc." }, statusCode: 503);
    }
});

app.MapPost("/api/catalog/reference/{kind}", async (string kind, ReferenceCreateRequest body,
    HttpRequest req, SessionService sessions, IConfiguration config, CancellationToken ct) =>
{
    if (!app.Environment.IsDevelopment()) return Results.NotFound();
    var who = sessions.Validate(ReadBearerToken(req));
    if (who is null) return Results.Unauthorized();
    if (who.Role is not ("Staff" or "Admin")) return Results.StatusCode(403);
    string name = body.Name?.Trim() ?? "";
    string detail = body.Detail?.Trim() ?? "";
    int limit = kind == "categories" ? 160 : 220;
    if (kind is not ("authors" or "publishers" or "categories")) return Results.NotFound();
    if (name.Length < 2 || name.Length > limit || detail.Length > 4000)
        return Results.BadRequest(new { message = "Ten 2-" + limit + " ky tu; mo ta toi da 4000." });
    if (kind == "categories" && detail.Length > 0)
        return Results.BadRequest(new { message = "Buoc nay chi tao the loai goc, de trong mo ta." });
    try
    {
        await using var db = new NpgsqlConnection(config.GetConnectionString("AivenPostgres"));
        await db.OpenAsync(ct);
        // identifiers are fixed choices, never accepted directly from client.
        string existsSql = kind switch
        {
            "authors" => "SELECT author_id FROM public.authors WHERE LOWER(name)=LOWER(@name) LIMIT 1",
            "publishers" => "SELECT publisher_id FROM public.publishers WHERE LOWER(name)=LOWER(@name) LIMIT 1",
            _ => "SELECT category_id FROM public.categories WHERE LOWER(name)=LOWER(@name) LIMIT 1"
        };
        await using (var check = new NpgsqlCommand(existsSql, db))
        {
            check.Parameters.AddWithValue("name", name);
            if (await check.ExecuteScalarAsync(ct) is not null)
                return Results.Conflict(new { message = "Ten nay da ton tai." });
        }
        string sql = kind switch
        {
            "authors" => "INSERT INTO public.authors(name,biography) VALUES(@name,@detail) RETURNING author_id",
            "publishers" => "INSERT INTO public.publishers(name,contact_info) VALUES(@name,@detail) RETURNING publisher_id",
            _ => "INSERT INTO public.categories(name,parent_id) VALUES(@name,NULL) RETURNING category_id"
        };
        await using var cmd = new NpgsqlCommand(sql, db);
        cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.AddWithValue("detail", string.IsNullOrEmpty(detail) ? DBNull.Value : (object)detail);
        long id = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        return Results.Ok(new { id, name, message = "created" });
    }
    catch (Exception ex)
    {
        app.Logger.LogError("Catalog reference create failed: {Type}", ex.GetType().Name);
        return Results.Json(new { message = "Khong the luu danh muc." }, statusCode: 503);
    }
});


// Admin-only staff management. Never accept a Role or role_id from the client.
app.MapGet("/api/admin/staff", async (HttpRequest request, SessionService sessions,
    IConfiguration config, CancellationToken ct) =>
{
    if (!app.Environment.IsDevelopment()) return Results.NotFound();
    var actor = sessions.Validate(ReadBearerToken(request));
    if (actor is null) return Results.Unauthorized();
    if (actor.Role != "Admin") return Results.StatusCode(403);
    try
    {
        await using var db = new NpgsqlConnection(config.GetConnectionString("AivenPostgres"));
        await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            SELECT u.user_id,u.username,s.staff_code,s.full_name,u.account_status,r.role_name
            FROM public.staff_profiles s JOIN public.users u ON s.user_id=u.user_id
            JOIN public.roles r ON u.role_id=r.role_id
            ORDER BY u.user_id LIMIT 200", db);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        var data = new List<object>();
        while (await rd.ReadAsync(ct))
            data.Add(new
            {
                userId = rd.GetInt64(0),
                username = rd.GetString(1),
                staffCode = rd.GetString(2),
                fullName = rd.GetString(3),
                status = rd.GetString(4),
                role = rd.GetString(5)
            });
        return Results.Ok(new { staff = data });
    }
    catch (Exception e)
    {
        app.Logger.LogError("Staff list failed: {Type}", e.GetType().Name);
        return Results.Json(new { message = "Khong the tai nhan vien." }, statusCode: 503);
    }
});

app.MapPost("/api/admin/staff", async (NewStaffRequest body, HttpRequest request,
    SessionService sessions, IConfiguration config, CancellationToken ct) =>
{
    if (!app.Environment.IsDevelopment()) return Results.NotFound();
    var actor = sessions.Validate(ReadBearerToken(request));
    if (actor is null) return Results.Unauthorized();
    if (actor.Role != "Admin") return Results.StatusCode(403);
    string username = body.Username?.Trim() ?? "";
    string fullname = body.FullName?.Trim() ?? "";
    string code = body.StaffCode?.Trim() ?? "";
    string password = body.Password ?? "";
    if (!System.Text.RegularExpressions.Regex.IsMatch(username, @"\A[a-zA-Z0-9._]{3,40}\z") ||
        fullname.Length is < 2 or > 200 || code.Length is < 2 or > 40 ||
        password.Length is < 5 or > 128)
        return Results.BadRequest(new { message = "Tai khoan 3-40; ho ten 2-200; ma NV 2-40; mat khau 5-128 ky tu." });
    try
    {
        await using var db = new NpgsqlConnection(config.GetConnectionString("AivenPostgres"));
        await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(91736125)", db, tx))
            await lockCmd.ExecuteNonQueryAsync(ct);
        await using (var check = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM public.users WHERE LOWER(username)=LOWER(@u))", db, tx))
        {
            check.Parameters.AddWithValue("u", username);
            if (Convert.ToBoolean(await check.ExecuteScalarAsync(ct)))
                return Results.Conflict(new { message = "Username da ton tai." });
        }
        long roleId;
        await using (var role = new NpgsqlCommand("SELECT role_id FROM public.roles WHERE LOWER(role_name)='staff' LIMIT 1", db, tx))
            roleId = Convert.ToInt64(await role.ExecuteScalarAsync(ct) ?? throw new InvalidOperationException("Staff role missing"));
        long userId;
        await using (var insert = new NpgsqlCommand(@"
            INSERT INTO public.users(username,password_hash,role_id,account_status)
            VALUES(@u,@h,@r,'active') RETURNING user_id", db, tx))
        {
            insert.Parameters.AddWithValue("u", username);
            insert.Parameters.AddWithValue("h", BCrypt.Net.BCrypt.EnhancedHashPassword(password, workFactor: 12));
            insert.Parameters.AddWithValue("r", checked((int)roleId));
            userId = Convert.ToInt64(await insert.ExecuteScalarAsync(ct));
        }
        await using (var insert = new NpgsqlCommand(@"
            INSERT INTO public.staff_profiles(user_id,staff_code,full_name,hourly_rate)
            VALUES(@id,@c,@f,0)", db, tx))
        {
            insert.Parameters.AddWithValue("id", userId);
            insert.Parameters.AddWithValue("c", code);
            insert.Parameters.AddWithValue("f", fullname);
            await insert.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return Results.Ok(new { message = "staff_created", userId, username, role = "Staff" });
    }
    catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
    {
        return Results.Conflict(new { message = "Username hoac ma nhan vien da ton tai." });
    }
    catch (Exception e)
    {
        app.Logger.LogError("Create staff failed: {Type}", e.GetType().Name);
        return Results.Json(new { message = "Khong the tao nhan vien." }, statusCode: 503);
    }
});


// STEP 52 — Reader profiles. No new database table or migration.
// A Reader sees only their OWN record; Staff/Admin can search limited basic details.
app.MapGet("/api/readers/me", async (HttpRequest request, SessionService sessions,
    IConfiguration configuration, CancellationToken cancellationToken) =>
{
    if (!app.Environment.IsDevelopment()) return Results.NotFound();
    var actor = sessions.Validate(ReadBearerToken(request));
    if (actor is null) return Results.Unauthorized();
    if (actor.Role != "Reader") return Results.StatusCode(StatusCodes.Status403Forbidden);
    try
    {
        string connectionString = configuration.GetConnectionString("AivenPostgres")
            ?? throw new InvalidOperationException("PostgreSQL connection string is missing.");
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        const string sql = @"
            SELECT u.user_id, u.username, rp.reader_code, rp.full_name,
                   COALESCE(rp.phone_e164,''), (rp.phone_verified_at IS NOT NULL),
                   t.tier_name, t.max_active_copies, t.max_loan_days, u.account_status
            FROM public.reader_profiles rp
            JOIN public.users u ON u.user_id = rp.user_id
            JOIN public.reader_tiers t ON t.tier_id = rp.tier_id
            WHERE u.user_id = @user_id";
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("user_id", actor.UserId); // Server session, never client input.
        await using var rd = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await rd.ReadAsync(cancellationToken)) return Results.NotFound();
        return Results.Ok(new
        {
            reader = new
            {
                userId = rd.GetInt64(0),
                username = rd.GetString(1),
                readerCode = rd.GetString(2),
                fullName = rd.GetString(3),
                phone = rd.GetString(4),
                phoneVerified = rd.GetBoolean(5),
                tierName = rd.GetString(6),
                maxActiveCopies = rd.GetInt32(7),
                maxLoanDays = rd.GetInt32(8),
                status = rd.GetString(9)
            }
        });
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
    }
    catch (Exception ex)
    {
        app.Logger.LogError("Reader self profile failed: {Type}", ex.GetType().Name);
        return Results.Json(new { message = "Khong the tai ho so doc gia." }, statusCode: 503);
    }
});

app.MapGet("/api/readers", async (string? query, HttpRequest request, SessionService sessions,
    IConfiguration configuration, CancellationToken cancellationToken) =>
{
    if (!app.Environment.IsDevelopment()) return Results.NotFound();
    var actor = sessions.Validate(ReadBearerToken(request));
    if (actor is null) return Results.Unauthorized();
    if (actor.Role is not ("Staff" or "Admin"))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    string keyword = (query ?? "").Trim();
    if (keyword.Length > 100)
        return Results.BadRequest(new { message = "Tu khoa khong duoc vuot qua 100 ky tu." });
    try
    {
        string connectionString = configuration.GetConnectionString("AivenPostgres")
            ?? throw new InvalidOperationException("PostgreSQL connection string is missing.");
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        const string sql = @"
            SELECT u.user_id, u.username, rp.reader_code, rp.full_name,
                   t.tier_name, u.account_status
            FROM public.reader_profiles rp
            JOIN public.users u ON u.user_id = rp.user_id
            JOIN public.reader_tiers t ON t.tier_id = rp.tier_id
            WHERE @keyword = ''
               OR u.username ILIKE @pattern
               OR rp.full_name ILIKE @pattern
               OR rp.reader_code ILIKE @pattern
            ORDER BY rp.user_id DESC
            LIMIT 100";
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("keyword", keyword);
        cmd.Parameters.AddWithValue("pattern", "%" + keyword + "%");
        await using var rd = await cmd.ExecuteReaderAsync(cancellationToken);
        var rows = new List<object>();
        while (await rd.ReadAsync(cancellationToken))
            rows.Add(new
            {
                userId = rd.GetInt64(0),
                username = rd.GetString(1),
                readerCode = rd.GetString(2),
                fullName = rd.GetString(3),
                tierName = rd.GetString(4),
                status = rd.GetString(5)
                // Do NOT expose Reader phone or citizen information to the list.
            });
        return Results.Ok(new { readers = rows });
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
    }
    catch (Exception ex)
    {
        app.Logger.LogError("Reader list failed: {Type}", ex.GetType().Name);
        return Results.Json(new { message = "Khong the tai danh sach doc gia." }, statusCode: 503);
    }
});


// STEP 53 — Reader self-service edits. OTP only printed in API terminal in Development.
// Never accept a target userId or permission role from the WPF client.
app.MapPost("/api/readers/me/edit/start", async (
    ReaderProfileEditStartRequest body, HttpRequest request, SessionService sessions,
    ReaderProfileEditOtpStore otpStore, IConfiguration config, CancellationToken ct) =>
{
    if (!app.Environment.IsDevelopment()) return Results.NotFound();
    var actor = sessions.Validate(ReadBearerToken(request));
    if (actor is null) return Results.Unauthorized();
    if (actor.Role != "Reader") return Results.StatusCode(StatusCodes.Status403Forbidden);

    string newName = (body.FullName ?? "").Trim();
    if (newName.Length is < 2 or > 200 || newName.Any(char.IsControl))
        return Results.BadRequest(new { message = "Ho ten phai dai 2-200 ky tu hop le." });
    string? phone = body.Phone?.Trim();
    if (!string.IsNullOrEmpty(phone))
    {
        phone = Regex.Replace(phone, @"[\s().-]", "");
        if (Regex.IsMatch(phone, @"\A0[35789]\d{8}\z")) phone = "+84" + phone[1..];
        if (!Regex.IsMatch(phone, @"\A\+84[35789]\d{8}\z"))
            return Results.BadRequest(new { message = "SDT Viet Nam khong hop le. VD: 0912345678." });
    }
    else phone = null;

    try
    {
        await using var db = new NpgsqlConnection(config.GetConnectionString("AivenPostgres"));
        await db.OpenAsync(ct);
        const string sql = @"
            SELECT rp.full_name, rp.phone_e164
            FROM public.reader_profiles rp JOIN public.users u ON u.user_id=rp.user_id
            WHERE rp.user_id=@user_id AND u.account_status='active';";
        string oldName;
        string? oldPhone;
        await using (var cmd = new NpgsqlCommand(sql, db))
        {
            cmd.Parameters.AddWithValue("user_id", actor.UserId);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return Results.NotFound();
            oldName = rd.GetString(0);
            oldPhone = rd.IsDBNull(1) ? null : rd.GetString(1);
        }
        if (oldPhone is not null && phone is null)
            return Results.BadRequest(new { message = "Khong the xoa so dien thoai da dang ky trong man hinh nay." });
        if (string.Equals(newName, oldName, StringComparison.Ordinal) &&
            string.Equals(phone, oldPhone, StringComparison.Ordinal))
            return Results.BadRequest(new { message = "Thong tin chua thay doi." });

        if (!otpStore.TryCreate(actor.UserId, oldName, oldPhone, newName, phone,
            out Guid requestId, out string otp, out DateTimeOffset expiresAt, out int retrySeconds))
            return Results.Json(new { message = $"Vui long thu lai sau {retrySeconds} giay." },
                statusCode: StatusCodes.Status429TooManyRequests);

        // THIS IS NOT REAL SMS DELIVERY AND DOES NOT PROVE PHONE OWNERSHIP.
        app.Logger.LogWarning("DEV ONLY — Reader {UserId} profile edit OTP: {Otp} (request {RequestId}). NOT SMS.",
            actor.UserId, otp, requestId);
        return Results.Ok(new
        {
            requestId,
            expiresAt,
            message = "Da tao OTP. Chi test: xem ma trong terminal SmartLibrary.Api, KHONG gui SMS."
        });
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
        return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
    }
    catch (Exception ex)
    {
        app.Logger.LogError("Reader edit start failed: {Type}", ex.GetType().Name);
        return Results.Json(new { message = "Khong tao duoc yeu cau doi ho so." }, statusCode: 503);
    }
});

app.MapPost("/api/readers/me/edit/confirm", async (
    ReaderProfileEditConfirmRequest body, HttpRequest request, SessionService sessions,
    ReaderProfileEditOtpStore otpStore, IConfiguration config, CancellationToken ct) =>
{
    if (!app.Environment.IsDevelopment()) return Results.NotFound();
    var actor = sessions.Validate(ReadBearerToken(request));
    if (actor is null) return Results.Unauthorized();
    if (actor.Role != "Reader") return Results.StatusCode(StatusCodes.Status403Forbidden);
    string code = body.Otp?.Trim() ?? "";
    if (!Regex.IsMatch(code, @"\A\d{6}\z") || body.RequestId == Guid.Empty)
        return Results.BadRequest(new { message = "Ma OTP gom dung 6 chu so." });
    var state = otpStore.Consume(actor.UserId, body.RequestId, code, out ReaderProfileEditChallenge? change);
    if (state != "ok" || change is null)
        return Results.BadRequest(new
        {
            message = state switch
            {
                "expired" => "Ma OTP het han. Vui long gui ma moi.",
                "wrong" => "Ma OTP khong dung. Toi da 5 lan thu.",
                "locked" => "Qua so lan nhap sai. Vui long yeu cau ma moi.",
                _ => "Khong co yeu cau OTP hop le cho phien nay."
            }
        });
    try
    {
        await using var db = new NpgsqlConnection(config.GetConnectionString("AivenPostgres"));
        await db.OpenAsync(ct);
        // Optimistic precondition: a second session cannot silently overwrite changed data.
        const string sql = @"
            UPDATE public.reader_profiles rp
            SET full_name=@new_name, phone_e164=@new_phone,
                phone_verified_at = CASE WHEN rp.phone_e164 IS DISTINCT FROM @new_phone
                    THEN CURRENT_TIMESTAMP ELSE rp.phone_verified_at END
            WHERE rp.user_id=@user_id
              AND rp.full_name=@old_name
              AND rp.phone_e164 IS NOT DISTINCT FROM @old_phone
              AND EXISTS (SELECT 1 FROM public.users u
                          WHERE u.user_id=rp.user_id AND u.account_status='active')
            RETURNING rp.user_id;";
        await using var cmd = new NpgsqlCommand(sql, db);
        cmd.Parameters.AddWithValue("user_id", actor.UserId);
        cmd.Parameters.Add("new_name", NpgsqlDbType.Varchar).Value = change.NewName;
        cmd.Parameters.Add("new_phone", NpgsqlDbType.Varchar).Value = (object?)change.NewPhone ?? DBNull.Value;
        cmd.Parameters.Add("old_name", NpgsqlDbType.Varchar).Value = change.OldName;
        cmd.Parameters.Add("old_phone", NpgsqlDbType.Varchar).Value = (object?)change.OldPhone ?? DBNull.Value;
        var updatedId = await cmd.ExecuteScalarAsync(ct);
        if (updatedId is null)
            return Results.Conflict(new { message = "Ho so da thay doi o phien khac. Hay tai lai va thuc hien lai." });
        return Results.Ok(new { message = "profile_updated" });
    }
    catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
    {
        return Results.Conflict(new { message = "So dien thoai da duoc tai khoan khac su dung." });
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
        return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
    }
    catch (Exception ex)
    {
        app.Logger.LogError("Reader edit confirm failed: {Type}", ex.GetType().Name);
        return Results.Json(new { message = "Khong cap nhat duoc ho so. Vui long tao lai OTP." }, statusCode: 503);
    }
});

app.Run();

// Du lieu JSON nhan tu WPF hoac Postman/PowerShell.
public sealed record LoginRequest(string? Username, string? Password);
public sealed record RegisterStartRequest(
    string? FullName,
    string? Username,
    string? Phone,
    string? Password);
public sealed record RegisterConfirmRequest(Guid RequestId, string? Otp);

public sealed record AddCopyRequest(string? CopyCode, string? ShelfLocation);

public sealed record ReferenceCreateRequest(string? Name, string? Detail);

public sealed record NewStaffRequest(string? Username, string? FullName, string? StaffCode, string? Password);

public sealed record ReaderProfileEditStartRequest(string? FullName, string? Phone);
public sealed record ReaderProfileEditConfirmRequest(Guid RequestId, string? Otp);

// In-memory DEV-only challenge, one pending change per account; no plaintext OTP stored.
// This is a simulator. Never replace real phone verification with console OTP in production.
public sealed record ReaderProfileEditChallenge(
    Guid Id, string OldName, string? OldPhone, string NewName, string? NewPhone,
    byte[] OtpHash, DateTimeOffset ExpiresAt, int RemainingAttempts);

public sealed class ReaderProfileEditOtpStore
{
    private readonly object _gate = new();
    private readonly Dictionary<long, ReaderProfileEditChallenge> _pending = new();
    private readonly Dictionary<long, DateTimeOffset> _lastIssued = new();
    private readonly byte[] _secret = RandomNumberGenerator.GetBytes(32);

    private byte[] Hash(Guid id, string code) => HMACSHA256.HashData(
        _secret, Encoding.UTF8.GetBytes(id.ToString("N") + ":" + code));

    public bool TryCreate(long userId, string oldName, string? oldPhone, string newName,
        string? newPhone, out Guid requestId, out string otp,
        out DateTimeOffset expiresAt, out int retrySeconds)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            requestId = Guid.Empty; otp = ""; expiresAt = now; retrySeconds = 0;
            if (_lastIssued.TryGetValue(userId, out var last) && now < last.AddSeconds(60))
            {
                retrySeconds = (int)Math.Ceiling((last.AddSeconds(60) - now).TotalSeconds);
                return false;
            }
            requestId = Guid.NewGuid();
            otp = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
            expiresAt = now.AddMinutes(5);
            _pending[userId] = new ReaderProfileEditChallenge(
                requestId, oldName, oldPhone, newName, newPhone, Hash(requestId, otp), expiresAt, 5);
            _lastIssued[userId] = now;
            return true;
        }
    }

    public string Consume(long userId, Guid requestId, string otp, out ReaderProfileEditChallenge? change)
    {
        lock (_gate)
        {
            change = null;
            if (!_pending.TryGetValue(userId, out var pending) || pending.Id != requestId)
                return "missing";
            if (pending.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                _pending.Remove(userId);
                return "expired";
            }
            if (!CryptographicOperations.FixedTimeEquals(Hash(requestId, otp), pending.OtpHash))
            {
                int remaining = pending.RemainingAttempts - 1;
                if (remaining <= 0) { _pending.Remove(userId); return "locked"; }
                _pending[userId] = pending with { RemainingAttempts = remaining };
                return "wrong";
            }
            _pending.Remove(userId); // single-use; no replay even if DB update fails.
            change = pending;
            return "ok";
        }
    }
}
