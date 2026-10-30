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
    IConfiguration configuration,
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

        // Guest is a library identity only, NEVER an interactive login. Fail closed.
        // The marker is server-owned; clients cannot elevate/change it.
        var loginCs = configuration.GetConnectionString("AivenPostgres")
            ?? throw new InvalidOperationException("Missing database configuration.");
        await using (var loginDb = new NpgsqlConnection(loginCs))
        {
            await loginDb.OpenAsync(cancellationToken);
            await using var guestGuard = new NpgsqlCommand(
                "SELECT is_guest FROM public.users WHERE user_id=@id;", loginDb);
            guestGuard.Parameters.AddWithValue("id", user.UserId);
            if (await guestGuard.ExecuteScalarAsync(cancellationToken) is not bool isGuest || isGuest)
                return Results.Json(new { message = "Sai thong tin dang nhap hoac tai khoan chua duoc kich hoat." },
                    statusCode: 401);
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
    catch (PostgresException exception)
    {
        // Only SQLSTATE is logged: never log credentials, SQL parameters or PII.
        app.Logger.LogError(
            "Login API PostgreSQL failure: SQLSTATE={SqlState}",
            exception.SqlState);

        return Results.Json(new
        {
            message = "Loi PostgreSQL khi dang nhap. Kiem tra migration Guest va SQLSTATE trong console API."
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
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
    app.MapPost("/api/auth/register/guest-preview", async (
        GuestPreviewRequest request,
        RegistrationService registrationService,
        CancellationToken cancellationToken) =>
    {
        try
        {
            var preview = await registrationService.PreviewGuestAsync(
                request.GuestClaimCode, cancellationToken);
            // Only a valid, unexpired Staff claim can reveal the existing name/phone.
            // Never return a raw CCCD, fingerprint, password, or borrowing history.
            return Results.Ok(new
            {
                message = "guest_preview_ready_dev_only",
                fullName = preview.FullName,
                phone = preview.Phone
            });
        }
        catch (RegistrationException ex)
        {
            return Results.Json(new { code = ex.Code, message = ex.Message },
                statusCode: ex.Code == "guest_claim_unavailable" ? 429 : 400);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Results.StatusCode(499);
        }
        catch (Exception ex)
        {
            app.Logger.LogError("Guest preview failed: {ErrorType}", ex.GetType().Name);
            return Results.Json(new { message = "Khong the tai ho so Guest luc nay." }, statusCode: 503);
        }
    });

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
                request.GuestClaimCode,
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
                   t.tier_name, u.account_status, u.is_guest
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
                status = rd.GetString(5),
                isGuest = rd.GetBoolean(6)
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



// STEP 63: Unified counter request. A Guest is a non-login Reader; an existing
// Reader/Guest uses the SAME user_id, history, borrow_requests and review pipeline.
// CCCD is hashed with the very same HMAC key as the Step-62 duplicate check.
app.MapPost("/api/borrow-requests/walkin", async (
    WalkInBorrowRequest input, HttpRequest request, SessionService sessions,
    IConfiguration config, CancellationToken ct) =>
{
    if (!app.Environment.IsDevelopment()) return Results.NotFound();
    var actor = sessions.Validate(ReadBearerToken(request));
    if (actor is null) return Results.Unauthorized();
    if (actor.Role is not ("Staff" or "Admin")) return Results.StatusCode(403);
    if (!input.StaffVerifiedIdentity)
        return Results.BadRequest(new { message = "Staff phải xác minh giấy tờ gốc hoặc xác nhận liên hệ trực tiếp tại quầy." });
    if (input.Items is null || input.Items.Count is < 1 or > 5 ||
       input.Items.Any(x => x.BookId <= 0 || x.RequestedDays is < 1 or > 90) ||
       input.Items.Select(x => x.BookId).Distinct().Count() != input.Items.Count ||
       (input.Note?.Length ?? 0) > 500)
        return Results.BadRequest(new { message = "Đơn cần 1–5 đầu sách khác nhau, thời hạn từ 1 đến 90 ngày." });
    string name = (input.FullName ?? "").Trim();
    string phoneRaw = (input.Phone ?? "").Trim();
    string citizen = (input.CitizenId ?? "").Trim();
    bool hasPhone = phoneRaw.Length > 0, hasCitizen = citizen.Length > 0;
    if (!hasPhone && !hasCitizen)
        return Results.BadRequest(new { message = "Phải có SĐT hoặc CCCD đã kiểm tra tại quầy." });
    if ((input.ExistingReaderUserId ?? 0) < 0 || (input.ExistingReaderUserId is null &&
       (name.Length < 2 || name.Length > 200)))
        return Results.BadRequest(new { message = "Tên Guest phải từ 2 đến 200 ký tự." });
    if (phoneRaw.Length > 24 || citizen.Length > 20 ||
       (hasCitizen && (citizen.Length != 12 || citizen.Any(c => c < '0' || c > '9'))))
        return Results.BadRequest(new { message = "Định dạng SĐT/CCCD không hợp lệ." });
    string phoneDigits = new string(phoneRaw.Where(char.IsDigit).ToArray());
    if (phoneDigits.StartsWith("84", StringComparison.Ordinal) && phoneDigits.Length == 11)
        phoneDigits = "0" + phoneDigits.Substring(2);
    if (hasPhone && (phoneRaw.Any(c => !char.IsDigit(c) && c != '+' && c != ' ' && c != '-') ||
                    phoneDigits.Length != 10 || phoneDigits[0] != '0'))
        return Results.BadRequest(new { message = "SĐT Việt Nam phải là 10 chữ số hoặc +84 tương đương." });
    string phoneCanonical = hasPhone ? "+84" + phoneDigits.Substring(1) : "";
    string[] phones = hasPhone ? new[] { phoneDigits, phoneCanonical, "84" + phoneDigits.Substring(1) } : Array.Empty<string>();
    byte[] fingerprint = Array.Empty<byte>();
    if (hasCitizen)
    {
        byte[] key;
        try { key = Convert.FromBase64String(config["GuestIdentity:HmacKeyBase64"] ?? ""); }
        catch (FormatException) { key = Array.Empty<byte>(); }
        if (key.Length < 32)
            return Results.Json(new { message = "Thiếu cấu hình GuestIdentity:HmacKeyBase64 trong User Secrets." }, statusCode: 503);
        using var hmac = new HMACSHA256(key);
        fingerprint = hmac.ComputeHash(Encoding.UTF8.GetBytes(citizen));
    }
    try
    {
        var cs = config.GetConnectionString("AivenPostgres")
            ?? throw new InvalidOperationException("Missing connection string.");
        await using var db = new NpgsqlConnection(cs);
        await db.OpenAsync(ct);
        // SERIALIZABLE + uniqueness ensure counter clerks cannot create duplicates concurrently.
        await using var tx = await db.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        var matches = new List<(long userId, bool byPhone, bool byCitizen)>();
        await using (var check = new NpgsqlCommand(@"
           SELECT rp.user_id,
                  COALESCE(rp.phone_e164=ANY(@phones),FALSE),
                  COALESCE(rp.citizen_id_fingerprint=@fp,FALSE)
           FROM public.reader_profiles rp
           WHERE (@phone AND rp.phone_e164=ANY(@phones))
              OR (@citizen AND rp.citizen_id_fingerprint=@fp)
           FOR UPDATE OF rp;", db, tx))
        {
            check.Parameters.Add("phones", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = phones;
            check.Parameters.Add("fp", NpgsqlDbType.Bytea).Value = fingerprint;
            check.Parameters.AddWithValue("phone", hasPhone);
            check.Parameters.AddWithValue("citizen", hasCitizen);
            await using var rd = await check.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) matches.Add((rd.GetInt64(0), rd.GetBoolean(1), rd.GetBoolean(2)));
        }
        long readerId;
        bool guestCreated = false;
        string? createdGuestCode = null;
        if (input.ExistingReaderUserId is long existingId)
        {
            // No blind lookup by user id: ALL supplied identifiers MUST match one and
            // only one existing profile. A family-shared phone cannot auto-link profiles.
            if (matches.Count != 1 || matches[0].userId != existingId ||
               (hasPhone && !matches[0].byPhone) || (hasCitizen && !matches[0].byCitizen))
                return Results.Conflict(new { message = "Hồ sơ được chọn không khớp toàn bộ định danh; xem chi tiết và xác minh tại quầy." });
            readerId = existingId;
        }
        else
        {
            if (matches.Count > 0)
                return Results.Conflict(new { message = "Đã tồn tại hồ sơ khớp SĐT/CCCD. KHÔNG tạo Guest trùng; chọn hồ sơ cũ sau khi xem lịch sử." });
            int roleId;
            await using (var role = new NpgsqlCommand("SELECT role_id FROM public.roles WHERE role_name='Reader';", db, tx))
            { var id = await role.ExecuteScalarAsync(ct); if (id is null) return Results.Json(new { message = "Thiếu quyền Reader." }, statusCode: 503); roleId = Convert.ToInt32(id); }
            long tierId;
            await using (var tier = new NpgsqlCommand("SELECT tier_id FROM public.reader_tiers WHERE is_default=TRUE AND is_active=TRUE;", db, tx))
            { var id = await tier.ExecuteScalarAsync(ct); if (id is null) return Results.Json(new { message = "Thiếu hạng Reader mặc định." }, statusCode: 503); tierId = Convert.ToInt64(id); }
            // Database sequence gives distinct human-readable Guest codes, even if
            // multiple clerks submit simultaneously. Gaps after rolled-back requests
            // are expected; codes must be unique, not gap-free.
            long guestNumber;
            await using (var numberCommand = new NpgsqlCommand(
                "SELECT nextval('public.guest_username_seq');", db, tx))
            {
                guestNumber = Convert.ToInt64(await numberCommand.ExecuteScalarAsync(ct));
            }
            string internalUsername = "guest" + guestNumber.ToString("D5", CultureInfo.InvariantCulture);
            createdGuestCode = internalUsername;
            // Intentionally invalid BCrypt hash plus is_guest guard: never an app credential.
            await using (var create = new NpgsqlCommand(@"
                INSERT INTO public.users(username,password_hash,role_id,account_status,is_guest)
                VALUES(@name,'!guest-no-password!',@role,'active',TRUE) RETURNING user_id;", db, tx))
            {
                create.Parameters.AddWithValue("name", internalUsername);
                create.Parameters.AddWithValue("role", roleId);
                readerId = Convert.ToInt64(await create.ExecuteScalarAsync(ct));
            }
            await using (var profile = new NpgsqlCommand(@"
                INSERT INTO public.reader_profiles
                  (user_id,reader_code,full_name,phone_e164,phone_verified_at,
                   citizen_id_fingerprint,citizen_verified_at,tier_id)
                VALUES(@uid,@code,@name,@phone,@phoneVerified,@fp,@citizenVerified,@tier);", db, tx))
            {
                profile.Parameters.AddWithValue("uid", readerId);
                // Show the same Guest identity in reader list, request history and username.
                profile.Parameters.AddWithValue("code", internalUsername);
                profile.Parameters.AddWithValue("name", name);
                profile.Parameters.AddWithValue("phone", (object?)(hasPhone ? phoneCanonical : null) ?? DBNull.Value);
                // Manual in-person verification: Staff must actually verify phone possession.
                profile.Parameters.AddWithValue("phoneVerified", (hasPhone ? (object)DateTime.UtcNow : DBNull.Value));
                profile.Parameters.Add("fp", NpgsqlDbType.Bytea).Value = hasCitizen ? (object)fingerprint : DBNull.Value;
                profile.Parameters.AddWithValue("citizenVerified", (hasCitizen ? (object)DateTime.UtcNow : DBNull.Value));
                profile.Parameters.AddWithValue("tier", tierId);
                await profile.ExecuteNonQueryAsync(ct);
            }
            guestCreated = true;
        }
        int copiesLimit, daysLimit;
        await using (var cap = new NpgsqlCommand(@"
            SELECT COALESCE(rp.override_max_active_copies,t.max_active_copies),
                   COALESCE(rp.override_max_loan_days,t.max_loan_days)
            FROM public.reader_profiles rp JOIN public.reader_tiers t ON t.tier_id=rp.tier_id
            JOIN public.users u ON u.user_id=rp.user_id
            WHERE rp.user_id=@uid AND u.account_status='active' AND t.is_active=TRUE
            FOR UPDATE OF rp;", db, tx))
        {
            cap.Parameters.AddWithValue("uid", readerId);
            await using var rd = await cap.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return Results.StatusCode(403);
            copiesLimit = rd.GetInt32(0); daysLimit = rd.GetInt32(1);
        }
        await using (var ban = new NpgsqlCommand(@"
            SELECT action FROM public.audit_logs WHERE entity_type='reader_borrow_access'
              AND entity_id=@reader AND action IN ('reader_borrow_block','reader_borrow_unblock')
            ORDER BY audit_id DESC LIMIT 1;", db, tx))
        {
            ban.Parameters.AddWithValue("reader", readerId.ToString(CultureInfo.InvariantCulture));
            if (await ban.ExecuteScalarAsync(ct) is string action && action == "reader_borrow_block")
                return Results.StatusCode(403);
        }
        if (input.Items.Any(x => x.RequestedDays > daysLimit))
            return Results.BadRequest(new { message = $"Hạng Reader chỉ cho mượn tối đa {daysLimit} ngày." });
        await using (var c = new NpgsqlCommand(@"
            SELECT (SELECT COUNT(*) FROM public.borrow_details d
                    JOIN public.borrows b ON b.borrow_id=d.borrow_id
                    WHERE b.reader_user_id=@uid AND d.closed_at IS NULL)
                 + (SELECT COUNT(*) FROM public.borrow_request_items i
                    JOIN public.borrow_requests br ON br.request_id=i.request_id
                    WHERE br.reader_user_id=@uid AND br.status='pending' AND i.status='pending');", db, tx))
        {
            c.Parameters.AddWithValue("uid", readerId);
            if (Convert.ToInt64(await c.ExecuteScalarAsync(ct)) + input.Items.Count > copiesLimit)
                return Results.Conflict(new { message = "Vượt hạn mức sách đang mượn/đơn chờ của độc giả." });
        }
        await using (var books = new NpgsqlCommand(@"
            SELECT COUNT(*) FROM public.books WHERE is_visible=TRUE AND book_id=ANY(@ids);", db, tx))
        {
            books.Parameters.Add("ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint).Value =
                input.Items.Select(x => x.BookId).ToArray();
            if (Convert.ToInt64(await books.ExecuteScalarAsync(ct)) != input.Items.Count)
                return Results.BadRequest(new { message = "Có đầu sách không tồn tại hoặc đã ẩn." });
        }
        string code = "BR-W-" + Guid.NewGuid().ToString("N")[..16].ToUpperInvariant();
        long requestId;
        await using (var req = new NpgsqlCommand(@"
            INSERT INTO public.borrow_requests
                (request_code,reader_user_id,status,note,origin)
            VALUES(@code,@uid,'pending',@note,'staff_walkin') RETURNING request_id;", db, tx))
        {
            req.Parameters.AddWithValue("code", code);
            req.Parameters.AddWithValue("uid", readerId);
            req.Parameters.AddWithValue("note", (object?)input.Note?.Trim() ?? DBNull.Value);
            requestId = Convert.ToInt64(await req.ExecuteScalarAsync(ct));
        }
        foreach (var item in input.Items)
        {
            await using var detail = new NpgsqlCommand(@"
                INSERT INTO public.borrow_request_items(request_id,book_id,requested_days,status)
                VALUES(@rid,@bid,@days,'pending');", db, tx);
            detail.Parameters.AddWithValue("rid", requestId);
            detail.Parameters.AddWithValue("bid", item.BookId);
            detail.Parameters.AddWithValue("days", item.RequestedDays);
            await detail.ExecuteNonQueryAsync(ct);
        }
        await using (var audit = new NpgsqlCommand(@"
            INSERT INTO public.audit_logs(actor_user_id,action,entity_type,entity_id,details)
            VALUES(@actor,'walkin_request_create','borrow_request',@request,
                   jsonb_build_object('reader_user_id',@reader,'guest_created',@guest,
                                      'identity_checked_at_counter',TRUE,'book_count',@total));", db, tx))
        {
            audit.Parameters.AddWithValue("actor", actor.UserId);
            audit.Parameters.AddWithValue("request", requestId.ToString(CultureInfo.InvariantCulture));
            audit.Parameters.AddWithValue("reader", readerId);
            audit.Parameters.AddWithValue("guest", guestCreated);
            audit.Parameters.AddWithValue("total", input.Items.Count);
            await audit.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return Results.Ok(new
        {
            message = guestCreated
            ? "Đã tạo hồ sơ Guest " + createdGuestCode + " (không đăng nhập) và đơn chờ duyệt."
            : "Đã dùng hồ sơ cũ và tạo đơn chờ duyệt.",
            readerUserId = readerId,
            requestId,
            requestCode = code,
            guestCreated,
            guestCode = createdGuestCode
        });
    }
    catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation ||
                                    e.SqlState == PostgresErrorCodes.SerializationFailure)
    { return Results.Conflict(new { message = "Dữ liệu vừa thay đổi hoặc định danh đã tồn tại. Tra cứu lại trước khi tiếp nhận." }); }
    catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UndefinedColumn ||
                                    e.SqlState == PostgresErrorCodes.UndefinedTable)
    { return Results.Json(new { message = "Chưa chạy migration bước 63 hoặc 58." }, statusCode: 503); }
    catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Results.StatusCode(499); }
    catch (Exception e)
    {
        app.Logger.LogError("Walk-in request failed: {Type}", e.GetType().Name);
        return Results.Json(new { message = "Không thể tạo đơn tại quầy. Không có dữ liệu nào được ghi nếu transaction thất bại." }, statusCode: 503);
    }
});

// STEP 62: Walk-in identity preflight. STAFF ONLY; no raw CCCD in URL/logs/responses.
// This is read-only: no Guest creation, identity merge, or checkout is performed here.
app.MapPost("/api/readers/identity-check", async (
    ReaderIdentityLookupRequest input, HttpRequest request, SessionService sessions,
    IConfiguration config, CancellationToken ct) =>
{
    if (!app.Environment.IsDevelopment()) return Results.NotFound();
    var actor = sessions.Validate(ReadBearerToken(request));
    if (actor is null) return Results.Unauthorized();
    if (actor.Role is not ("Staff" or "Admin")) return Results.StatusCode(403);

    string phoneRaw = (input.Phone ?? "").Trim();
    string citizenRaw = (input.CitizenId ?? "").Trim();
    if (phoneRaw.Length > 24 || citizenRaw.Length > 20)
        return Results.BadRequest(new { message = "So lien he/giay to khong hop le." });
    string phoneDigits = new string(phoneRaw.Where(char.IsDigit).ToArray());
    if (phoneDigits.StartsWith("84", StringComparison.Ordinal) && phoneDigits.Length == 11)
        phoneDigits = "0" + phoneDigits.Substring(2);
    bool hasPhone = phoneRaw.Length > 0;
    bool hasCitizen = citizenRaw.Length > 0;
    if (!hasPhone && !hasCitizen)
        return Results.BadRequest(new { message = "Nhap so dien thoai hoac CCCD de tra cuu." });
    if (hasPhone && (phoneDigits.Length != 10 || phoneDigits[0] != '0' ||
                     phoneRaw.Any(c => !char.IsDigit(c) && c != '+' && c != ' ' && c != '-')))
        return Results.BadRequest(new { message = "Nhap so dien thoai Viet Nam hop le (10 so hoac +84...)." });
    if (hasCitizen && (citizenRaw.Length != 12 || citizenRaw.Any(c => c < '0' || c > '9')))
        return Results.BadRequest(new { message = "CCCD phai gom 12 chu so." });

    byte[] fingerprint = Array.Empty<byte>();
    if (hasCitizen)
    {
        // Use the SAME stable server-only key/algorithm for future Guest creation.
        // If existing citizen fingerprints were created with another algorithm,
        // they cannot be matched until a controlled migration/re-verification.
        byte[] key;
        try { key = Convert.FromBase64String(config["GuestIdentity:HmacKeyBase64"] ?? ""); }
        catch (FormatException) { key = Array.Empty<byte>(); }
        if (key.Length < 32)
            return Results.Json(new { message = "Chua cau hinh GuestIdentity:HmacKeyBase64 trong backend User Secrets." }, statusCode: 503);
        using var hmac = new HMACSHA256(key);
        fingerprint = hmac.ComputeHash(Encoding.UTF8.GetBytes(citizenRaw));
    }
    string[] phones = hasPhone
        ? new[] { phoneDigits, "+84" + phoneDigits.Substring(1), "84" + phoneDigits.Substring(1) }
        : Array.Empty<string>();
    try
    {
        var cs = config.GetConnectionString("AivenPostgres")
            ?? throw new InvalidOperationException("Missing database connection string");
        await using var db = new NpgsqlConnection(cs);
        await db.OpenAsync(ct);
        const string sql = @"
            SELECT u.user_id,u.username,rp.reader_code,rp.full_name,u.account_status,
                   COALESCE((rp.phone_e164=ANY(@phones)),FALSE) AS matched_phone,
                   COALESCE((rp.citizen_id_fingerprint=@fingerprint),FALSE) AS matched_citizen,
                   (SELECT COUNT(*) FROM public.borrows b JOIN public.borrow_details d
                        ON d.borrow_id=b.borrow_id
                    WHERE b.reader_user_id=u.user_id AND d.closed_at IS NULL) AS open_copies,
                   (SELECT COUNT(*) FROM public.borrows b JOIN public.borrow_details d
                        ON d.borrow_id=b.borrow_id
                    WHERE b.reader_user_id=u.user_id AND d.closed_at IS NULL AND d.due_at<NOW()) AS overdue_copies,
                   (SELECT COUNT(*) FROM public.borrows b JOIN public.borrow_details d
                        ON d.borrow_id=b.borrow_id
                    WHERE b.reader_user_id=u.user_id AND d.close_reason='returned'
                      AND d.returned_at>d.due_at) AS late_returns,
                   (SELECT COUNT(*) FROM public.borrows b JOIN public.borrow_details d
                        ON d.borrow_id=b.borrow_id
                    WHERE b.reader_user_id=u.user_id AND d.close_reason='returned') AS completed_returns,
                   COALESCE((SELECT a.action FROM public.audit_logs a
                     WHERE a.entity_type='reader_borrow_access'
                       AND a.entity_id=u.user_id::text
                       AND a.action IN ('reader_borrow_block','reader_borrow_unblock')
                     ORDER BY a.audit_id DESC LIMIT 1),'') AS last_ban_decision
            FROM public.reader_profiles rp
            JOIN public.users u ON u.user_id=rp.user_id
            JOIN public.roles role ON role.role_id=u.role_id AND role.role_name='Reader'
            WHERE (@has_phone AND rp.phone_e164=ANY(@phones))
               OR (@has_citizen AND rp.citizen_id_fingerprint=@fingerprint)
            ORDER BY u.user_id LIMIT 6;";
        await using var cmd = new NpgsqlCommand(sql, db);
        cmd.Parameters.Add("phones", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = phones;
        cmd.Parameters.Add("fingerprint", NpgsqlDbType.Bytea).Value = fingerprint;
        cmd.Parameters.AddWithValue("has_phone", hasPhone);
        cmd.Parameters.AddWithValue("has_citizen", hasCitizen);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        var matches = new List<object>();
        bool identityConflict = false;
        while (await rd.ReadAsync(ct))
        {
            bool matchedPhone = rd.GetBoolean(5), matchedCitizen = rd.GetBoolean(6);
            // If BOTH are supplied, each must belong to the SAME profile.
            if (hasPhone && hasCitizen && !(matchedPhone && matchedCitizen)) identityConflict = true;
            long overdue = rd.GetInt64(8), late = rd.GetInt64(9), returned = rd.GetInt64(10);
            string ban = rd.GetString(11);
            bool blocked = ban == "reader_borrow_block";
            string risk = blocked ? "blocked" : overdue > 0 ? "red"
                : late == 0 && returned > 0 ? "green"
                : late <= 2 && late > 0 ? "yellow"
                : late > 2 ? "orange" : "neutral";
            matches.Add(new
            {
                userId = rd.GetInt64(0),
                username = rd.GetString(1),
                readerCode = rd.GetString(2),
                fullName = rd.GetString(3),
                accountStatus = rd.GetString(4),
                matchReason = matchedPhone && matchedCitizen ? "Trung SĐT va CCCD"
                    : matchedPhone ? "Trung SĐT" : "Trung CCCD",
                openCopies = rd.GetInt64(7),
                overdueCopies = overdue,
                lateReturns = late,
                completedReturns = returned,
                borrowBlocked = blocked,
                riskLevel = risk
            });
        }
        // Shared family phone OR two identifiers belonging to different people: NEVER auto-link.
        bool conflict = matches.Count > 1 || identityConflict;
        return Results.Ok(new
        {
            matches,
            matchedCount = matches.Count,
            conflict,
            message = matches.Count == 0 ? "Chua tim thay ho so trung. Chua phai xac nhan khach moi."
                : conflict ? "Canh bao: SĐT/CCCD khong cung khop mot ho so, hoac co nhieu ho so. Khong duoc tu lien ket."
                : "Da co ho so voi thong tin trung. Xem lich su truoc khi tiep nhan."
        });
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Results.StatusCode(499); }
    catch (Exception ex)
    {
        app.Logger.LogError("Walk-in identity preflight failed: {Type}", ex.GetType().Name);
        return Results.Json(new { message = "Khong tra cuu duoc ho so. Khong tao Guest moi khi chua kiem tra trung." }, statusCode: 503);
    }
});

// STEP 57 - Staff/Admin reader insights and borrow ban history. Uses existing audit_logs,
// no DDL, no new column. Ban is an event-sourced decision distinct from login status.
app.MapGet("/api/readers/{readerId:long}/insights", async (long readerId,
    HttpRequest request, SessionService sessions, IConfiguration config, CancellationToken ct) =>
{
    if (!app.Environment.IsDevelopment()) return Results.NotFound();
    var actor = sessions.Validate(ReadBearerToken(request));
    if (actor is null) return Results.Unauthorized();
    if (actor.Role is not ("Staff" or "Admin")) return Results.StatusCode(403);
    if (readerId <= 0) return Results.BadRequest(new { message = "ID doc gia khong hop le." });
    string insightStage = "reader_profile";
    try
    {
        var cs = config.GetConnectionString("AivenPostgres")
            ?? throw new InvalidOperationException("Database connection missing.");
        await using var db = new NpgsqlConnection(cs);
        await db.OpenAsync(ct);
        string? username = null, fullName = null, readerCode = null, tier = null, status = null, phone = null;
        int maxCopies = 0, maxDays = 0;
        await using (var cmd = new NpgsqlCommand(@"
            SELECT u.username,rp.full_name,rp.reader_code,t.tier_name,u.account_status,
                   COALESCE(rp.phone_e164,''),
                   COALESCE(rp.override_max_active_copies,t.max_active_copies),
                   COALESCE(rp.override_max_loan_days,t.max_loan_days)
            FROM public.reader_profiles rp
            JOIN public.users u ON u.user_id=rp.user_id
            JOIN public.reader_tiers t ON t.tier_id=rp.tier_id
            JOIN public.roles role ON role.role_id=u.role_id
            WHERE rp.user_id=@id AND role.role_name='Reader';", db))
        {
            cmd.Parameters.AddWithValue("id", readerId);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return Results.NotFound();
            username = rd.GetString(0); fullName = rd.GetString(1); readerCode = rd.GetString(2);
            tier = rd.GetString(3); status = rd.GetString(4); phone = rd.GetString(5);
            maxCopies = rd.GetInt32(6); maxDays = rd.GetInt32(7);
        }
        insightStage = "loan_summary";
        long totalLoans = 0, totalCopies = 0, returned = 0, open = 0, overdue = 0;
        await using (var cmd = new NpgsqlCommand(@"
            SELECT COUNT(DISTINCT b.borrow_id),COUNT(d.borrow_detail_id),
                   COUNT(d.borrow_detail_id) FILTER(WHERE d.close_reason='returned'),
                   COUNT(d.borrow_detail_id) FILTER(WHERE d.closed_at IS NULL),
                   COUNT(d.borrow_detail_id) FILTER(WHERE
                     (d.close_reason='returned' AND d.returned_at>d.due_at)
                     OR (d.closed_at IS NULL AND d.due_at<NOW()))
            FROM public.borrows b JOIN public.borrow_details d ON d.borrow_id=b.borrow_id
            WHERE b.reader_user_id=@id;", db))
        {
            cmd.Parameters.AddWithValue("id", readerId);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            await rd.ReadAsync(ct);
            totalLoans = rd.GetInt64(0); totalCopies = rd.GetInt64(1);
            returned = rd.GetInt64(2); open = rd.GetInt64(3); overdue = rd.GetInt64(4);
        }
        insightStage = "fine_balances";
        decimal fineTotal = 0, fineOwed = 0;
        await using (var cmd = new NpgsqlCommand(@"
            SELECT COALESCE(SUM(GREATEST(0,f.amount-f.waived_amount)),0),
                   COALESCE(SUM(GREATEST(0,f.amount-f.waived_amount-COALESCE(pay.paid,0))),0)
            FROM public.fines f
            JOIN public.borrow_details d ON d.borrow_detail_id=f.borrow_detail_id
            JOIN public.borrows b ON b.borrow_id=d.borrow_id
            LEFT JOIN (SELECT fine_id,SUM(amount) paid FROM public.fine_payments GROUP BY fine_id)
              pay ON pay.fine_id=f.fine_id
            WHERE b.reader_user_id=@id;", db))
        {
            cmd.Parameters.AddWithValue("id", readerId);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            await rd.ReadAsync(ct);
            fineTotal = rd.GetDecimal(0); fineOwed = rd.GetDecimal(1);
        }
        insightStage = "loan_history";
        var history = new List<object>();
        await using (var cmd = new NpgsqlCommand(@"
            SELECT bk.title,bc.copy_code,b.borrowed_at,d.due_at,
                   d.returned_at,d.closed_at,d.close_reason
            FROM public.borrow_details d
            JOIN public.borrows b ON b.borrow_id=d.borrow_id
            JOIN public.book_copies bc ON bc.copy_id=d.book_copy_id
            JOIN public.books bk ON bk.book_id=bc.book_id
            WHERE b.reader_user_id=@id
            ORDER BY b.borrowed_at DESC,d.borrow_detail_id DESC LIMIT 100;", db))
        {
            cmd.Parameters.AddWithValue("id", readerId);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                DateTime borrow = rd.GetDateTime(2), due = rd.GetDateTime(3);
                DateTime? ret = rd.IsDBNull(4) ? null : rd.GetDateTime(4);
                bool closed = !rd.IsDBNull(5);
                bool late = (ret.HasValue && ret.Value > due) || (!closed && due < DateTime.UtcNow);
                history.Add(new
                {
                    title = rd.GetString(0),
                    copyCode = rd.GetString(1),
                    borrowedAt = borrow.ToUniversalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture),
                    dueAt = due.ToUniversalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture),
                    returnedAt = ret.HasValue ? ret.Value.ToUniversalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture) : "",
                    status = rd.IsDBNull(6) ? (closed ? "Đã đóng" : late ? "Đang quá hạn" : "Đang mượn") : rd.GetString(6),
                    isOverdue = late
                });
            }
        }
        insightStage = "borrow_access_history";
        var decisions = new List<object>(); bool blocked = false; bool first = true;
        await using (var cmd = new NpgsqlCommand(@"
            SELECT a.action,COALESCE(a.details->>'reason',''),
                   a.created_at,COALESCE(u.username,'system')
            FROM public.audit_logs a
            LEFT JOIN public.users u ON u.user_id=a.actor_user_id
            WHERE a.entity_type='reader_borrow_access' AND a.entity_id=@entity
              AND a.action IN ('reader_borrow_block','reader_borrow_unblock')
            ORDER BY a.audit_id DESC LIMIT 40;", db))
        {
            cmd.Parameters.AddWithValue("entity", readerId.ToString(CultureInfo.InvariantCulture));
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                bool isBlock = rd.GetString(0) == "reader_borrow_block";
                if (first) { blocked = isBlock; first = false; }
                decisions.Add(new
                {
                    action = isBlock ? "Cấm mượn" : "Mở cấm",
                    reason = rd.GetString(1),
                    at = rd.GetDateTime(2).ToUniversalTime().ToString("dd/MM/yyyy HH:mm 'UTC'", CultureInfo.InvariantCulture),
                    by = rd.GetString(3)
                });
            }
        }
        // Last six UTC months; count copies borrowed, including zero months.
        insightStage = "monthly_chart";
        var monthCounts = new Dictionary<string, long>();
        await using (var cmd = new NpgsqlCommand(@"
            SELECT to_char(date_trunc('month',b.borrowed_at AT TIME ZONE 'UTC'),'YYYY-MM') AS month_key,
                   COUNT(*)
            FROM public.borrows b JOIN public.borrow_details d ON d.borrow_id=b.borrow_id
            WHERE b.reader_user_id=@id AND b.borrowed_at>=@since
            GROUP BY 1 ORDER BY 1;", db))
        {
            var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-5);
            cmd.Parameters.AddWithValue("id", readerId);
            cmd.Parameters.AddWithValue("since", NpgsqlDbType.TimestampTz, monthStart);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) monthCounts[rd.GetString(0)] = rd.GetInt64(1);
        }
        var chart = new List<object>();
        var start = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).AddMonths(-5);
        for (int i = 0; i < 6; i++)
        {
            var m = start.AddMonths(i); string key = m.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            chart.Add(new
            {
                month = m.ToString("MM/yyyy", CultureInfo.InvariantCulture),
                count = monthCounts.TryGetValue(key, out long n) ? n : 0L
            });
        }
        return Results.Ok(new
        {
            reader = new
            {
                userId = readerId,
                username,
                fullName,
                readerCode,
                tierName = tier,
                accountStatus = status,
                phone,
                maxActiveCopies = maxCopies,
                maxLoanDays = maxDays,
                borrowBlocked = blocked
            },
            statistics = new
            {
                totalLoans,
                totalCopies,
                returnedCopies = returned,
                activeCopies = open,
                overdueCount = overdue,
                totalFine = fineTotal,
                outstandingFine = fineOwed
            },
            loans = history,
            restrictions = decisions,
            monthlyBorrowCounts = chart
        });
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Results.StatusCode(499); }
    catch (PostgresException ex)
    {
        // Server log contains actual SQLSTATE and Postgres message. Do NOT log secrets or SQL parameters.
        app.Logger.LogError(ex,
            "Reader insight PostgreSQL error at {Stage}. SQLSTATE={SqlState}; PG Message={PgMessage}",
            insightStage, ex.SqlState, ex.MessageText);
        // The API is loopback/Development only; expose safe error location to assist diagnosis.
        return Results.Json(new
        {
            message = "Không tải được chi tiết tại mục " + insightStage +
                      " (PostgreSQL " + ex.SqlState + "). Kiểm tra terminal API.",
            stage = insightStage,
            sqlState = ex.SqlState
        }, statusCode: 503);
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Reader insight error at {Stage}", insightStage);
        return Results.Json(new { message = "Không tải được chi tiết độc giả tại mục " + insightStage + "." }, statusCode: 503);
    }
});

// STEP 65: Staff-assisted claim, NOT automatic merging by matching phone/CCCD.
// Only works in Development with physical identity checking at the counter.
app.MapPost("/api/readers/{readerId:long}/guest-claim", async (
    long readerId, GuestClaimIssueRequest body, HttpRequest request,
    SessionService sessions, IConfiguration config, RegistrationService registration,
    CancellationToken ct) =>
{
    if (!app.Environment.IsDevelopment()) return Results.NotFound();
    var actor = sessions.Validate(ReadBearerToken(request));
    if (actor is null) return Results.Unauthorized();
    if (actor.Role is not ("Staff" or "Admin")) return Results.StatusCode(403);
    if (readerId <= 0 || body is null || !body.StaffVerifiedIdentity)
        return Results.BadRequest(new { message = "Nhân viên phải trực tiếp kiểm tra giấy tờ hoặc quyền sở hữu thông tin liên hệ." });
    try
    {
        var cs = config.GetConnectionString("AivenPostgres")
            ?? throw new InvalidOperationException("Missing database configuration.");
        await using var db = new NpgsqlConnection(cs);
        await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await using (var check = new NpgsqlCommand(@"
            SELECT u.user_id FROM public.users u
            JOIN public.roles role ON role.role_id = u.role_id
            JOIN public.reader_profiles rp ON rp.user_id = u.user_id
            WHERE u.user_id=@id AND u.is_guest=TRUE
              AND u.account_status='active' AND role.role_name='Reader'
            FOR UPDATE OF u, rp;", db, tx))
        {
            check.Parameters.AddWithValue("id", readerId);
            if (await check.ExecuteScalarAsync(ct) is null)
                return Results.Conflict(new { message = "Hồ sơ không phải Guest đang hoạt động hoặc đã được liên kết." });
        }
        // Audit the human attestation; never write activation code or identity number to audit.
        await using (var log = new NpgsqlCommand(@"
            INSERT INTO public.audit_logs(actor_user_id,action,entity_type,entity_id,details)
            VALUES(@actor,'guest_claim_issued','guest_activation',@entity,
                   '{""verification"":""staff_attested_at_counter""}'::jsonb);", db, tx))
        {
            log.Parameters.AddWithValue("actor", actor.UserId);
            log.Parameters.AddWithValue("entity", readerId.ToString(CultureInfo.InvariantCulture));
            await log.ExecuteNonQueryAsync(ct);
        }
        var claim = registration.IssueGuestClaim(readerId, actor.UserId);
        await tx.CommitAsync(ct);
        return Results.Ok(new
        {
            message = "guest_claim_issued_dev_only",
            guestClaimCode = claim.Code,
            expiresAt = claim.ExpiresAt
        });
    }
    catch (RegistrationException ex)
    {
        return Results.Json(new { message = ex.Message }, statusCode: 409);
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Results.StatusCode(499); }
    catch (Exception ex)
    {
        app.Logger.LogError("Guest claim issue failed: {Type}", ex.GetType().Name);
        return Results.Json(new { message = "Không cấp được mã liên kết Guest." }, statusCode: 503);
    }
});

// Ban/unban is serialized with checkout on the same reader_profiles row lock.
// The authoritative state is the LATEST audit event, not any WPF field.
app.MapPost("/api/readers/{readerId:long}/borrow-access", async (
    long readerId, ReaderBorrowAccessRequest body, HttpRequest request,
    SessionService sessions, IConfiguration config, CancellationToken ct) =>
{
    if (!app.Environment.IsDevelopment()) return Results.NotFound();
    var actor = sessions.Validate(ReadBearerToken(request));
    if (actor is null) return Results.Unauthorized();
    if (actor.Role is not ("Staff" or "Admin")) return Results.StatusCode(403);
    string reason = (body.Reason ?? "").Trim();
    if (readerId <= 0 || reason.Length < 8 || reason.Length > 500)
        return Results.BadRequest(new { message = "Nhap ly do tu 8 den 500 ky tu." });
    try
    {
        var cs = config.GetConnectionString("AivenPostgres")
            ?? throw new InvalidOperationException("Database connection missing.");
        await using var db = new NpgsqlConnection(cs);
        await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await using (var lockCmd = new NpgsqlCommand(@"
            SELECT 1 FROM public.reader_profiles rp
            JOIN public.users u ON u.user_id=rp.user_id
            WHERE rp.user_id=@id AND u.account_status='active'
            FOR UPDATE OF rp;", db, tx))
        {
            lockCmd.Parameters.AddWithValue("id", readerId);
            if (await lockCmd.ExecuteScalarAsync(ct) is null)
                return Results.BadRequest(new { message = "Khong tim thay Reader active." });
        }
        bool currentBlock = false;
        await using (var check = new NpgsqlCommand(@"
            SELECT action FROM public.audit_logs
            WHERE entity_type='reader_borrow_access' AND entity_id=@entity
              AND action IN ('reader_borrow_block','reader_borrow_unblock')
            ORDER BY audit_id DESC LIMIT 1;", db, tx))
        {
            check.Parameters.AddWithValue("entity", readerId.ToString(CultureInfo.InvariantCulture));
            currentBlock = (await check.ExecuteScalarAsync(ct)) as string == "reader_borrow_block";
        }
        if (currentBlock == body.Block)
            return Results.Conflict(new { message = currentBlock ? "Doc gia dang bi cam muon." : "Doc gia hien khong bi cam muon." });
        await using (var save = new NpgsqlCommand(@"
            INSERT INTO public.audit_logs(actor_user_id,action,entity_type,entity_id,details)
            VALUES(@actor,@action,'reader_borrow_access',@entity,jsonb_build_object('reason',@reason));", db, tx))
        {
            save.Parameters.AddWithValue("actor", actor.UserId);
            save.Parameters.AddWithValue("action", body.Block ? "reader_borrow_block" : "reader_borrow_unblock");
            save.Parameters.AddWithValue("entity", readerId.ToString(CultureInfo.InvariantCulture));
            save.Parameters.AddWithValue("reason", reason);
            await save.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return Results.Ok(new { message = body.Block ? "Da cam Reader muon sach moi." : "Da mo cam Reader muon sach." });
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Results.StatusCode(499); }
    catch (Exception ex)
    {
        app.Logger.LogError("Reader borrow access error {Type}", ex.GetType().Name);
        return Results.Json(new { message = "Khong cap nhat duoc quyen muon." }, statusCode: 503);
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


// STEP 54 — Lending one physical copy at a time. All routes are localhost DEV only.
// Reader may inspect ONLY their own loans. Staff/Admin may inspect and perform checkout/return.
app.MapGet("/api/loans", async (HttpRequest request, SessionService sessions,
    IConfiguration configuration, CancellationToken ct) =>
{
    if (!app.Environment.IsDevelopment()) return Results.NotFound();
    var actor = sessions.Validate(ReadBearerToken(request));
    if (actor is null) return Results.Unauthorized();
    if (actor.Role is not ("Reader" or "Staff" or "Admin")) return Results.StatusCode(403);
    try
    {
        var cs = configuration.GetConnectionString("AivenPostgres")
            ?? throw new InvalidOperationException("Database is not configured.");
        await using var db = new NpgsqlConnection(cs);
        await db.OpenAsync(ct);
        const string sql = @"
            SELECT bd.borrow_detail_id, b.borrow_id, u.username, rp.full_name,
                   bc.copy_code, bk.title, b.borrowed_at, bd.due_at,
                   bd.returned_at, bd.closed_at,
                   CASE WHEN bd.close_reason='returned' THEN 'Đã trả'
                        WHEN bd.close_reason='lost' THEN 'Báo mất'
                        WHEN bd.closed_at IS NOT NULL THEN 'Đã đóng'
                        WHEN bd.due_at < NOW() THEN 'Quá hạn' ELSE 'Đang mượn' END
            FROM public.borrow_details bd
            JOIN public.borrows b ON b.borrow_id=bd.borrow_id
            JOIN public.users u ON u.user_id=b.reader_user_id
            JOIN public.reader_profiles rp ON rp.user_id=u.user_id
            JOIN public.book_copies bc ON bc.copy_id=bd.book_copy_id
            JOIN public.books bk ON bk.book_id=bc.book_id
            WHERE (@is_reader = FALSE OR b.reader_user_id=@reader_id)
            ORDER BY (bd.closed_at IS NULL) DESC, b.borrowed_at DESC, bd.borrow_detail_id DESC LIMIT 100;";
        await using var cmd = new NpgsqlCommand(sql, db);
        cmd.Parameters.AddWithValue("is_reader", actor.Role == "Reader");
        cmd.Parameters.AddWithValue("reader_id", actor.UserId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<object>();
        while (await reader.ReadAsync(ct))
        {
            DateTime borrowed = reader.GetDateTime(6);
            DateTime due = reader.GetDateTime(7);
            string status = reader.GetString(10);
            rows.Add(new
            {
                borrowDetailId = reader.GetInt64(0),
                borrowId = reader.GetInt64(1),
                readerUsername = reader.GetString(2),
                readerName = reader.GetString(3),
                copyCode = reader.GetString(4),
                bookTitle = reader.GetString(5),
                borrowedAt = borrowed.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture),
                dueAt = due.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture),
                returnedAt = reader.IsDBNull(8) ? "" : reader.GetDateTime(8).ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture),
                isOpen = reader.IsDBNull(9),
                status
            });
        }
        return Results.Ok(new { loans = rows });
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    { return Results.StatusCode(499); }
    catch (Exception ex)
    {
        app.Logger.LogError("Load loans failed: {Type}", ex.GetType().Name);
        return Results.Json(new { message = "Khong the tai phieu muon." }, statusCode: 503);
    }
});

// STEP 55 — DEV-only autocomplete: staff/admin can find Reader without accents/case.
// Sensitive fields (phone, ID, password) are intentionally not returned.
app.MapGet("/api/loans/reader-suggestions", async (string? query, HttpRequest request,
    SessionService sessions, IConfiguration configuration, CancellationToken ct) =>
{
    if (!app.Environment.IsDevelopment()) return Results.NotFound();
    var actor = sessions.Validate(ReadBearerToken(request));
    if (actor is null) return Results.Unauthorized();
    if (actor.Role is not ("Staff" or "Admin")) return Results.StatusCode(403);
    string keyword = (query ?? "").Trim();
    if (keyword.Length > 80) return Results.BadRequest(new { message = "Tu khoa toi da 80 ky tu." });
    if (keyword.Length == 0) return Results.Ok(new { readers = Array.Empty<object>() });
    try
    {
        var cs = configuration.GetConnectionString("AivenPostgres")
            ?? throw new InvalidOperationException("Database is not configured.");
        await using var db = new NpgsqlConnection(cs);
        await db.OpenAsync(ct);
        const string sql = @"
            SELECT u.username, rp.full_name, rp.reader_code
            FROM public.reader_profiles rp
            JOIN public.users u ON u.user_id=rp.user_id
            JOIN public.roles r ON r.role_id=u.role_id
            JOIN public.reader_tiers t ON t.tier_id=rp.tier_id
            WHERE u.account_status='active' AND r.role_name='Reader' AND t.is_active=TRUE
            ORDER BY rp.full_name, u.username;";
        await using var cmd = new NpgsqlCommand(sql, db);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<object>();
        string needle = VietnameseSearch.Fold(keyword);
        while (await reader.ReadAsync(ct))
        {
            string username = reader.GetString(0), fullName = reader.GetString(1), code = reader.GetString(2);
            if (!VietnameseSearch.Matches(username, needle) &&
                !VietnameseSearch.Matches(fullName, needle) &&
                !VietnameseSearch.Matches(code, needle)) continue;
            rows.Add(new { username, fullName, readerCode = code });
            if (rows.Count >= 30) break;
        }
        return Results.Ok(new { readers = rows });
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    { return Results.StatusCode(499); }
    catch (Exception ex)
    {
        app.Logger.LogError("Reader suggestion search failed: {Type}", ex.GetType().Name);
        return Results.Json(new { message = "Khong the tim doc gia." }, statusCode: 503);
    }
});


// STEP 56 — Autocomplete ALL catalogue titles, even when no lendable physical copy exists.
// Demo/localhost endpoint, Staff/Admin only. Actual checkout still requires an available copy.
app.MapGet("/api/loans/book-suggestions", async (string? query, HttpRequest request,
    SessionService sessions, IConfiguration configuration, CancellationToken ct) =>
{
    if (!app.Environment.IsDevelopment()) return Results.NotFound();
    var actor = sessions.Validate(ReadBearerToken(request));
    if (actor is null) return Results.Unauthorized();
    if (actor.Role is not ("Staff" or "Admin")) return Results.StatusCode(403);
    string keyword = (query ?? "").Trim();
    if (keyword.Length > 80) return Results.BadRequest(new { message = "Tu khoa toi da 80 ky tu." });
    if (keyword.Length == 0) return Results.Ok(new { books = Array.Empty<object>() });
    try
    {
        var cs = configuration.GetConnectionString("AivenPostgres")
                 ?? throw new InvalidOperationException("Database is not configured.");
        await using var db = new NpgsqlConnection(cs);
        await db.OpenAsync(ct);
        // The catalogue is intentionally searched independently from physical copies.
        // For a larger library, move accent folding to indexed PostgreSQL search.
        const string sql = @"
            SELECT b.book_id, b.title, COALESCE(b.isbn,''),
                   COALESCE((SELECT string_agg(a.name, ', ' ORDER BY a.name)
                         FROM public.book_authors ba
                         JOIN public.authors a ON a.author_id=ba.author_id
                         WHERE ba.book_id=b.book_id), ''),
                   COALESCE((SELECT string_agg(c.name, ', ' ORDER BY c.name)
                         FROM public.book_categories bc
                         JOIN public.categories c ON c.category_id=bc.category_id
                         WHERE bc.book_id=b.book_id), ''),
                   COALESCE(p.name,''),
                   (SELECT COUNT(*) FROM public.book_copies bc
                       WHERE bc.book_id=b.book_id AND bc.condition_status='active'
                         AND NOT EXISTS (SELECT 1 FROM public.borrow_details d
                                         WHERE d.book_copy_id=bc.copy_id AND d.closed_at IS NULL)),
                   COALESCE((SELECT bc.copy_code FROM public.book_copies bc
                       WHERE bc.book_id=b.book_id AND bc.condition_status='active'
                         AND NOT EXISTS (SELECT 1 FROM public.borrow_details d
                                         WHERE d.book_copy_id=bc.copy_id AND d.closed_at IS NULL)
                       ORDER BY bc.copy_code LIMIT 1), '')
            FROM public.books b
            LEFT JOIN public.publishers p ON p.publisher_id=b.publisher_id
            WHERE b.is_visible=TRUE
            ORDER BY b.title, b.book_id;";
        await using var cmd = new NpgsqlCommand(sql, db);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        string needle = VietnameseSearch.Fold(keyword);
        var books = new List<object>();
        while (await reader.ReadAsync(ct))
        {
            string title = reader.GetString(1), isbn = reader.GetString(2);
            string authors = reader.GetString(3), categories = reader.GetString(4);
            string publisher = reader.GetString(5);
            if (!VietnameseSearch.Matches(title, needle) &&
                !VietnameseSearch.Matches(isbn, needle) &&
                !VietnameseSearch.Matches(authors, needle) &&
                !VietnameseSearch.Matches(categories, needle) &&
                !VietnameseSearch.Matches(publisher, needle)) continue;
            books.Add(new
            {
                bookId = reader.GetInt64(0),
                title,
                isbn,
                authors,
                categories,
                publisher,
                availableCopies = reader.GetInt64(6),
                firstAvailableCopyCode = reader.GetString(7)
            });
            if (books.Count >= 30) break;
        }
        return Results.Ok(new { books });
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    { return Results.StatusCode(499); }
    catch (Exception ex)
    {
        app.Logger.LogError("Book suggestion search failed: {Type}", ex.GetType().Name);
        return Results.Json(new { message = "Khong the tai goi y sach." }, statusCode: 503);
    }
});

// The copy list below also supports Unicode accent/case-insensitive partial matches.
// This is a bounded RESULT scan in .NET and needs indexed DB search at large scale.
app.MapGet("/api/loans/available-copies", async (string? query, HttpRequest request,
    SessionService sessions, IConfiguration configuration, CancellationToken ct) =>
{
    if (!app.Environment.IsDevelopment()) return Results.NotFound();
    var actor = sessions.Validate(ReadBearerToken(request));
    if (actor is null) return Results.Unauthorized();
    if (actor.Role is not ("Staff" or "Admin")) return Results.StatusCode(403);
    string keyword = (query ?? "").Trim();
    if (keyword.Length > 80) return Results.BadRequest(new { message = "Tu khoa toi da 80 ky tu." });
    try
    {
        var cs = configuration.GetConnectionString("AivenPostgres")
            ?? throw new InvalidOperationException("Database is not configured.");
        await using var db = new NpgsqlConnection(cs);
        await db.OpenAsync(ct);
        const string sql = @"
            SELECT bc.copy_id, bc.copy_code, b.title, COALESCE(bc.shelf_location,'')
            FROM public.book_copies bc
            JOIN public.books b ON b.book_id=bc.book_id
            WHERE bc.condition_status='active' AND b.is_visible=TRUE
              AND NOT EXISTS(SELECT 1 FROM public.borrow_details d
                             WHERE d.book_copy_id=bc.copy_id AND d.closed_at IS NULL)
            ORDER BY b.title, bc.copy_code;";
        await using var cmd = new NpgsqlCommand(sql, db);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<object>();
        string needle = VietnameseSearch.Fold(keyword);
        while (await reader.ReadAsync(ct))
        {
            string copyCode = reader.GetString(1), title = reader.GetString(2);
            if (needle.Length > 0 && !VietnameseSearch.Matches(copyCode, needle) &&
                !VietnameseSearch.Matches(title, needle)) continue;
            rows.Add(new
            {
                copyId = reader.GetInt64(0),
                copyCode,
                bookTitle = title,
                shelf = reader.GetString(3)
            });
            if (rows.Count >= 100) break;
        }
        return Results.Ok(new { copies = rows });
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    { return Results.StatusCode(499); }
    catch (Exception ex)
    {
        app.Logger.LogError("Available copies failed: {Type}", ex.GetType().Name);
        return Results.Json(new { message = "Khong the tai danh sach cuon san co." }, statusCode: 503);
    }
});

app.MapPost("/api/loans/checkout", async (CheckoutLoanRequest input,
    HttpRequest request, SessionService sessions, IConfiguration configuration,
    CancellationToken ct) =>
{
    if (!app.Environment.IsDevelopment()) return Results.NotFound();
    var actor = sessions.Validate(ReadBearerToken(request));
    if (actor is null) return Results.Unauthorized();
    if (actor.Role is not ("Staff" or "Admin")) return Results.StatusCode(403);
    string username = (input.ReaderUsername ?? "").Trim();
    string copyCode = (input.CopyCode ?? "").Trim();
    if (username.Length is < 3 or > 100 || copyCode.Length is < 1 or > 60 ||
        input.LoanDays is < 1 or > 90)
        return Results.BadRequest(new { message = "Username 3-100, ma cuon 1-60, so ngay muon 1-90." });
    try
    {
        var cs = configuration.GetConnectionString("AivenPostgres")
            ?? throw new InvalidOperationException("Database is not configured.");
        await using var db = new NpgsqlConnection(cs);
        await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);

        // Lock reader profile FIRST. All checkout requests follow the same lock order;
        // concurrent checkouts for one Reader cannot bypass the tier copy limit.
        long readerId, tierId;
        int maxCopies, maxDays;
        await using (var cmd = new NpgsqlCommand(@"
            SELECT u.user_id, rp.tier_id,
                   COALESCE(rp.override_max_active_copies,t.max_active_copies),
                   COALESCE(rp.override_max_loan_days,t.max_loan_days)
            FROM public.reader_profiles rp
            JOIN public.users u ON u.user_id=rp.user_id
            JOIN public.roles r ON r.role_id=u.role_id
            JOIN public.reader_tiers t ON t.tier_id=rp.tier_id
            WHERE LOWER(u.username)=LOWER(@username) AND u.account_status='active'
                  AND u.is_guest=FALSE -- Guest must go through the shared request/review flow
                  AND r.role_name='Reader' AND t.is_active=TRUE
            FOR UPDATE OF rp;", db, tx))
        {
            cmd.Parameters.AddWithValue("username", username);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return Results.BadRequest(new { message = "Khong tim thay Reader dang hoat dong." });
            readerId = reader.GetInt64(0); tierId = reader.GetInt64(1);
            maxCopies = reader.GetInt32(2); maxDays = reader.GetInt32(3);
        }
        // Deny new checkout if the latest staff decision banned this Reader.
        // The profile row was locked above; ban/unban uses the same lock.
        await using (var access = new NpgsqlCommand(@"
            SELECT action FROM public.audit_logs
            WHERE entity_type='reader_borrow_access' AND entity_id=@entity
              AND action IN ('reader_borrow_block','reader_borrow_unblock')
            ORDER BY audit_id DESC LIMIT 1;", db, tx))
        {
            access.Parameters.AddWithValue("entity", readerId.ToString(CultureInfo.InvariantCulture));
            if ((await access.ExecuteScalarAsync(ct)) as string == "reader_borrow_block")
                return Results.StatusCode(StatusCodes.Status403Forbidden);
        }
        if (input.LoanDays > maxDays)
            return Results.BadRequest(new { message = $"Hang doc gia chi duoc muon toi da {maxDays} ngay." });

        await using (var cmd = new NpgsqlCommand(@"
            SELECT COUNT(*) FROM public.borrow_details d
            JOIN public.borrows b ON b.borrow_id=d.borrow_id
            WHERE b.reader_user_id=@reader_id AND d.closed_at IS NULL;", db, tx))
        {
            cmd.Parameters.AddWithValue("reader_id", readerId);
            long current = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
            if (current >= maxCopies)
                return Results.Conflict(new { message = $"Doc gia da dat gioi han {maxCopies} cuon dang muon." });
        }

        // Lock physical copy; unique partial index is an additional guarantee.
        long copyId;
        await using (var cmd = new NpgsqlCommand(@"
            SELECT bc.copy_id FROM public.book_copies bc
            JOIN public.books b ON b.book_id=bc.book_id
            WHERE bc.copy_code=@copy AND bc.condition_status='active' AND b.is_visible=TRUE
            FOR UPDATE OF bc;", db, tx))
        {
            cmd.Parameters.AddWithValue("copy", copyCode);
            var value = await cmd.ExecuteScalarAsync(ct);
            if (value is null)
                return Results.BadRequest(new { message = "Ma cuon khong ton tai hoac cuon khong duoc phep muon." });
            copyId = Convert.ToInt64(value);
        }
        await using (var cmd = new NpgsqlCommand(@"
            SELECT EXISTS(SELECT 1 FROM public.borrow_details
                          WHERE book_copy_id=@copy_id AND closed_at IS NULL);", db, tx))
        {
            cmd.Parameters.AddWithValue("copy_id", copyId);
            if ((bool)(await cmd.ExecuteScalarAsync(ct) ?? false))
                return Results.Conflict(new { message = "Cuon nay dang duoc muon." });
        }
        var borrowedAt = DateTime.UtcNow;
        var dueAt = borrowedAt.AddDays(input.LoanDays);
        long borrowId;
        await using (var cmd = new NpgsqlCommand(@"
            INSERT INTO public.borrows(reader_user_id,issued_by_user_id,borrowed_at)
            VALUES(@reader,@staff,@borrowed) RETURNING borrow_id;", db, tx))
        {
            cmd.Parameters.AddWithValue("reader", readerId);
            cmd.Parameters.AddWithValue("staff", actor.UserId);
            cmd.Parameters.AddWithValue("borrowed", NpgsqlDbType.TimestampTz, borrowedAt);
            borrowId = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        }
        long detailId;
        await using (var cmd = new NpgsqlCommand(@"
            INSERT INTO public.borrow_details
                (borrow_id,book_copy_id,selected_loan_days,initial_due_at,due_at,
                 applied_tier_id,applied_max_loan_days,applied_max_active_copies)
            VALUES(@borrow,@copy,@days,@due,@due,@tier,@max_days,@max_copies)
            RETURNING borrow_detail_id;", db, tx))
        {
            cmd.Parameters.AddWithValue("borrow", borrowId);
            cmd.Parameters.AddWithValue("copy", copyId);
            cmd.Parameters.AddWithValue("days", input.LoanDays);
            cmd.Parameters.AddWithValue("due", NpgsqlDbType.TimestampTz, dueAt);
            cmd.Parameters.AddWithValue("tier", tierId);
            cmd.Parameters.AddWithValue("max_days", maxDays);
            cmd.Parameters.AddWithValue("max_copies", maxCopies);
            detailId = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        }
        await tx.CommitAsync(ct);
        return Results.Ok(new
        {
            message = $"Da cho {username} muon cuon {copyCode} trong {input.LoanDays} ngay.",
            borrowId,
            borrowDetailId = detailId,
            dueAt = dueAt.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)
        });
    }
    catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
    { return Results.Conflict(new { message = "Ma cuon dang co phieu muon mo. Vui long tai lai." }); }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    { return Results.StatusCode(499); }
    catch (Exception ex)
    {
        app.Logger.LogError("Checkout failed: {Type}", ex.GetType().Name);
        return Results.Json(new { message = "Khong the tao phieu muon." }, statusCode: 503);
    }
});

app.MapPost("/api/loans/{borrowDetailId:long}/return", async (long borrowDetailId,
    HttpRequest request, SessionService sessions, IConfiguration configuration,
    CancellationToken ct) =>
{
    if (!app.Environment.IsDevelopment()) return Results.NotFound();
    var actor = sessions.Validate(ReadBearerToken(request));
    if (actor is null) return Results.Unauthorized();
    if (actor.Role is not ("Staff" or "Admin")) return Results.StatusCode(403);
    if (borrowDetailId <= 0) return Results.BadRequest(new { message = "Ma chi tiet muon khong hop le." });
    try
    {
        var cs = configuration.GetConnectionString("AivenPostgres")
            ?? throw new InvalidOperationException("Database is not configured.");
        await using var db = new NpgsqlConnection(cs);
        await db.OpenAsync(ct);
        // Atomic conditional update prevents double return and respects ck_borrow_detail_close.
        await using var cmd = new NpgsqlCommand(@"
            UPDATE public.borrow_details
            SET returned_at=NOW(), closed_at=NOW(), close_reason='returned'
            WHERE borrow_detail_id=@detail AND closed_at IS NULL
            RETURNING borrow_detail_id;", db);
        cmd.Parameters.AddWithValue("detail", borrowDetailId);
        var value = await cmd.ExecuteScalarAsync(ct);
        if (value is null)
            return Results.Conflict(new { message = "Phieu khong ton tai hoac cuon da duoc tra." });
        return Results.Ok(new { message = "Da ghi nhan tra cuon thanh cong.", borrowDetailId });
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    { return Results.StatusCode(499); }
    catch (Exception ex)
    {
        app.Logger.LogError("Return loan failed: {Type}", ex.GetType().Name);
        return Results.Json(new { message = "Khong the ghi nhan tra sach." }, statusCode: 503);
    }
});


// STEP 58 — Reader creates a multi-title REQUEST; this does not reserve copies or create borrows.
// All new routes stay Development-only, matching the existing localhost prototype.
app.MapGet("/api/borrow-requests/books", async (string? query, HttpRequest request,
    SessionService sessions, IConfiguration config, CancellationToken ct) =>
{
    if (!app.Environment.IsDevelopment()) return Results.NotFound();
    var actor = sessions.Validate(ReadBearerToken(request));
    if (actor is null) return Results.Unauthorized();
    if (actor.Role is not ("Reader" or "Staff" or "Admin")) return Results.StatusCode(403);
    string term = (query ?? "").Trim();
    if (term.Length > 80) return Results.BadRequest(new { message = "Tu khoa toi da 80 ky tu." });
    try
    {
        var cs = config.GetConnectionString("AivenPostgres")
            ?? throw new InvalidOperationException("Database config missing.");
        await using var db = new NpgsqlConnection(cs);
        await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            SELECT b.book_id, b.title, COALESCE(b.isbn,''),
                   COALESCE((SELECT string_agg(a.name, ', ' ORDER BY a.name)
                      FROM public.book_authors ba
                      JOIN public.authors a ON a.author_id=ba.author_id
                      WHERE ba.book_id=b.book_id),''),
                   (SELECT COUNT(*) FROM public.book_copies bc
                    WHERE bc.book_id=b.book_id AND bc.condition_status='active'
                      AND NOT EXISTS(SELECT 1 FROM public.borrow_details d
                                     WHERE d.book_copy_id=bc.copy_id AND d.closed_at IS NULL))
            FROM public.books b WHERE b.is_visible=TRUE ORDER BY b.title LIMIT 2000;", db);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var found = new List<object>();
        string folded = VietnameseSearch.Fold(term);
        while (await reader.ReadAsync(ct))
        {
            var title = reader.GetString(1); var isbn = reader.GetString(2);
            var authors = reader.GetString(3);
            if (folded.Length > 0 &&
                !VietnameseSearch.Matches(title, folded) &&
                !VietnameseSearch.Matches(isbn, folded) &&
                !VietnameseSearch.Matches(authors, folded)) continue;
            found.Add(new
            {
                bookId = reader.GetInt64(0),
                title,
                isbn,
                authors,
                availableCopies = reader.GetInt64(4)
            });
            if (found.Count >= 25) break;
        }
        return Results.Ok(new { books = found });
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Results.StatusCode(499); }
    catch (Exception ex)
    {
        app.Logger.LogError("Request book search failed: {Type}", ex.GetType().Name);
        return Results.Json(new { message = "Khong tim duoc dau sach." }, statusCode: 503);
    }
});

app.MapGet("/api/borrow-requests", async (HttpRequest request, SessionService sessions,
    IConfiguration config, CancellationToken ct) =>
{
    if (!app.Environment.IsDevelopment()) return Results.NotFound();
    var actor = sessions.Validate(ReadBearerToken(request));
    if (actor is null) return Results.Unauthorized();
    if (actor.Role is not ("Reader" or "Staff" or "Admin")) return Results.StatusCode(403);
    try
    {
        var cs = config.GetConnectionString("AivenPostgres")
            ?? throw new InvalidOperationException("Database config missing.");
        await using var db = new NpgsqlConnection(cs);
        await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            SELECT br.request_id,br.request_code,u.username,br.status,br.requested_at,
                   COALESCE(br.note,''),COUNT(i.request_item_id),
                   COALESCE(string_agg(b.title || ' (' || i.requested_days || ' ngay)',
                                      '; ' ORDER BY i.request_item_id),''),
                   (SELECT COUNT(*) FROM public.borrow_details bd
                     JOIN public.borrows bo ON bo.borrow_id=bd.borrow_id
                     WHERE bo.reader_user_id=br.reader_user_id
                       AND bd.returned_at IS NOT NULL AND bd.returned_at>bd.due_at),
                   (SELECT COUNT(*) FROM public.borrow_details bd
                     JOIN public.borrows bo ON bo.borrow_id=bd.borrow_id
                     WHERE bo.reader_user_id=br.reader_user_id
                       AND bd.closed_at IS NULL AND bd.due_at<NOW()),
                   COALESCE((SELECT rp.full_name FROM public.reader_profiles rp
                             WHERE rp.user_id=br.reader_user_id),u.username),
                   br.reader_user_id,
                   br.origin,
                   (SELECT COUNT(*) FROM public.borrow_details bd
                     JOIN public.borrows bo ON bo.borrow_id=bd.borrow_id
                     WHERE bo.reader_user_id=br.reader_user_id
                       AND bd.returned_at IS NOT NULL)
            FROM public.borrow_requests br
            JOIN public.users u ON u.user_id=br.reader_user_id
            LEFT JOIN public.borrow_request_items i ON i.request_id=br.request_id
            LEFT JOIN public.books b ON b.book_id=i.book_id
            WHERE (@reader_only=FALSE OR br.reader_user_id=@reader_id)
            GROUP BY br.request_id,u.username
            ORDER BY br.requested_at DESC,br.request_id DESC LIMIT 100;", db);
        cmd.Parameters.AddWithValue("reader_only", actor.Role == "Reader");
        cmd.Parameters.AddWithValue("reader_id", actor.UserId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var result = new List<object>();
        while (await reader.ReadAsync(ct)) result.Add(new
        {
            requestId = reader.GetInt64(0),
            requestCode = reader.GetString(1),
            readerUsername = reader.GetString(2),
            status = reader.GetString(3),
            requestedAt = reader.GetDateTime(4).ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture),
            note = reader.GetString(5),
            itemCount = reader.GetInt64(6),
            itemsSummary = reader.GetString(7),
            lateReturns = reader.GetInt64(8),
            openOverdue = reader.GetInt64(9),
            readerFullName = reader.GetString(10),
            readerUserId = reader.GetInt64(11),
            completedReturns = reader.GetInt64(13),
            origin = reader.GetString(12),
            riskLevel = reader.GetInt64(9) > 0 ? "red" : reader.GetInt64(8) > 0 && reader.GetInt64(8) <= 2 ? "yellow"
                : reader.GetInt64(8) > 2 ? "orange" : reader.GetInt64(13) > 0 ? "green" : "neutral"
        });
        return Results.Ok(new { requests = result });
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Results.StatusCode(499); }
    catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
    { return Results.Json(new { message = "Chua chay SQL migration buoc 58." }, statusCode: 503); }
    catch (Exception ex)
    {
        app.Logger.LogError("Request list failed: {Type}", ex.GetType().Name);
        return Results.Json(new { message = "Khong tai duoc don muon." }, statusCode: 503);
    }
});

app.MapPost("/api/borrow-requests", async (NewBorrowRequest input, HttpRequest request,
    SessionService sessions, IConfiguration config, CancellationToken ct) =>
{
    if (!app.Environment.IsDevelopment()) return Results.NotFound();
    var actor = sessions.Validate(ReadBearerToken(request));
    if (actor is null) return Results.Unauthorized();
    if (actor.Role != "Reader") return Results.StatusCode(403);
    if (input.Items is null || input.Items.Count is < 1 or > 5 ||
       input.Items.Any(x => x.BookId <= 0 || x.RequestedDays is < 1 or > 90) ||
       input.Items.Select(x => x.BookId).Distinct().Count() != input.Items.Count ||
       (input.Note?.Length ?? 0) > 500)
        return Results.BadRequest(new { message = "Mot don co 1-5 dau sach khac nhau, so ngay 1-90, ghi chu toi da 500 ky tu." });
    try
    {
        var cs = config.GetConnectionString("AivenPostgres")
            ?? throw new InvalidOperationException("Database config missing.");
        await using var db = new NpgsqlConnection(cs);
        await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        int limit, daysLimit;
        // Same lock as ban/unban and checkout, so the check is server-authoritative.
        await using (var cmd = new NpgsqlCommand(@"
            SELECT COALESCE(rp.override_max_active_copies,t.max_active_copies),
                   COALESCE(rp.override_max_loan_days,t.max_loan_days)
            FROM public.reader_profiles rp
            JOIN public.reader_tiers t ON t.tier_id=rp.tier_id
            JOIN public.users u ON u.user_id=rp.user_id
            JOIN public.roles r ON r.role_id=u.role_id
            WHERE rp.user_id=@id AND u.account_status='active'
              AND r.role_name='Reader' AND t.is_active=TRUE
            FOR UPDATE OF rp;", db, tx))
        {
            cmd.Parameters.AddWithValue("id", actor.UserId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return Results.StatusCode(403);
            limit = reader.GetInt32(0); daysLimit = reader.GetInt32(1);
        }
        await using (var cmd = new NpgsqlCommand(@"
            SELECT action FROM public.audit_logs
            WHERE entity_type='reader_borrow_access' AND entity_id=@id
            AND action IN ('reader_borrow_block','reader_borrow_unblock')
            ORDER BY audit_id DESC LIMIT 1;", db, tx))
        {
            cmd.Parameters.AddWithValue("id", actor.UserId.ToString(CultureInfo.InvariantCulture));
            if ((await cmd.ExecuteScalarAsync(ct)) as string == "reader_borrow_block")
                return Results.StatusCode(403);
        }
        if (input.Items.Any(x => x.RequestedDays > daysLimit))
            return Results.BadRequest(new { message = $"Hang doc gia cho phep toi da {daysLimit} ngay." });
        await using (var cmd = new NpgsqlCommand(@"
            SELECT (SELECT COUNT(*) FROM public.borrow_details d
                    JOIN public.borrows b ON b.borrow_id=d.borrow_id
                    WHERE b.reader_user_id=@id AND d.closed_at IS NULL)
                 + (SELECT COUNT(*) FROM public.borrow_request_items i
                    JOIN public.borrow_requests br ON br.request_id=i.request_id
                    WHERE br.reader_user_id=@id AND br.status='pending'
                      AND i.status='pending');", db, tx))
        {
            cmd.Parameters.AddWithValue("id", actor.UserId);
            var outstanding = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
            if (outstanding + input.Items.Count > limit)
                return Results.Conflict(new { message = $"Dang muon/cho duyet {outstanding} cuon; gioi han cua ban la {limit}." });
        }
        // Reject hidden/deleted titles. A pending request is allowed even at zero physical copies;
        // staff can reject it later. No physical copy is reserved by this endpoint.
        await using (var cmd = new NpgsqlCommand(@"
            SELECT COUNT(*) FROM public.books WHERE book_id=ANY(@ids) AND is_visible=TRUE;", db, tx))
        {
            cmd.Parameters.Add("ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint)
                .Value = input.Items.Select(x => x.BookId).ToArray();
            if (Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)) != input.Items.Count)
                return Results.BadRequest(new { message = "Mot so dau sach khong ton tai hoac da an." });
        }
        long requestId;
        string code = "BR-" + DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture)
            + "-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        await using (var cmd = new NpgsqlCommand(@"
            INSERT INTO public.borrow_requests(request_code,reader_user_id,status,note)
            VALUES(@code,@id,'pending',@note) RETURNING request_id;", db, tx))
        {
            cmd.Parameters.AddWithValue("code", code);
            cmd.Parameters.AddWithValue("id", actor.UserId);
            cmd.Parameters.AddWithValue("note", (object?)input.Note?.Trim() ?? DBNull.Value);
            requestId = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        }
        foreach (var item in input.Items)
        {
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO public.borrow_request_items(request_id,book_id,requested_days,status)
                VALUES(@request,@book,@days,'pending');", db, tx);
            cmd.Parameters.AddWithValue("request", requestId);
            cmd.Parameters.AddWithValue("book", item.BookId);
            cmd.Parameters.AddWithValue("days", item.RequestedDays);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return Results.Ok(new { message = "Da gui don muon. Cho nhan vien duyet, chua giu cho cuon sach.", requestId, requestCode = code });
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Results.StatusCode(499); }
    catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
    { return Results.Json(new { message = "Chua chay SQL migration buoc 58." }, statusCode: 503); }
    catch (Exception ex)
    {
        app.Logger.LogError("Create borrow request failed: {Type}", ex.GetType().Name);
        return Results.Json(new { message = "Khong the tao don muon." }, statusCode: 503);
    }
});


// STEP 59: staff review entire multi-title request; all-or-nothing checkout transaction.
// A request is NOT a loan until all actual physical copies are assigned here.
app.MapPost("/api/borrow-requests/{requestId:long}/review", async (
    long requestId, ReviewBorrowRequest body, HttpRequest request,
    SessionService sessions, IConfiguration config, CancellationToken ct) =>
{
    if (!app.Environment.IsDevelopment()) return Results.NotFound();
    var actor = sessions.Validate(ReadBearerToken(request));
    if (actor is null) return Results.Unauthorized();
    if (actor.Role is not ("Staff" or "Admin")) return Results.StatusCode(403);
    if (requestId <= 0 || (body.Note?.Length ?? 0) > 500 ||
        (!body.Approve && string.IsNullOrWhiteSpace(body.Note)))
        return Results.BadRequest(new { message = "Tu choi phai co ly do; ghi chu toi da 500 ky tu." });
    try
    {
        var cs = config.GetConnectionString("AivenPostgres")
            ?? throw new InvalidOperationException("Database configuration missing");
        await using var db = new NpgsqlConnection(cs);
        await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        // Read owner, then lock reader profile FIRST to serialize with checkout and borrow bans.
        long owner;
        await using (var ownerCmd = new NpgsqlCommand(
            "SELECT reader_user_id FROM public.borrow_requests WHERE request_id=@id", db, tx))
        {
            ownerCmd.Parameters.AddWithValue("id", requestId);
            var ownerValue = await ownerCmd.ExecuteScalarAsync(ct);
            if (ownerValue is null) return Results.NotFound();
            owner = Convert.ToInt64(ownerValue);
        }
        int maxCopies, maxDays;
        long tierId;
        await using (var cap = new NpgsqlCommand(@"
            SELECT rp.tier_id, COALESCE(rp.override_max_active_copies,t.max_active_copies),
                   COALESCE(rp.override_max_loan_days,t.max_loan_days)
            FROM public.reader_profiles rp
            JOIN public.reader_tiers t ON t.tier_id=rp.tier_id
            JOIN public.users u ON u.user_id=rp.user_id
            WHERE rp.user_id=@user AND u.account_status='active' AND t.is_active=TRUE
            FOR UPDATE OF rp;", db, tx))
        {
            cap.Parameters.AddWithValue("user", owner);
            await using var rr = await cap.ExecuteReaderAsync(ct);
            if (!await rr.ReadAsync(ct)) return Results.StatusCode(403);
            tierId = rr.GetInt64(0); maxCopies = rr.GetInt32(1); maxDays = rr.GetInt32(2);
        }
        await using (var req = new NpgsqlCommand(@"
            SELECT status FROM public.borrow_requests WHERE request_id=@id
            AND reader_user_id=@owner FOR UPDATE;", db, tx))
        {
            req.Parameters.AddWithValue("id", requestId);
            req.Parameters.AddWithValue("owner", owner);
            if ((await req.ExecuteScalarAsync(ct)) as string != "pending")
                return Results.Conflict(new { message = "Don khong con o trang thai cho duyet." });
        }
        if (!body.Approve)
        {
            await using (var reject = new NpgsqlCommand(@"
                UPDATE public.borrow_requests
                SET status='rejected',reviewed_at=NOW(),reviewed_by_user_id=@actor,review_note=@note
                WHERE request_id=@id;", db, tx))
            {
                reject.Parameters.AddWithValue("id", requestId);
                reject.Parameters.AddWithValue("actor", actor.UserId);
                reject.Parameters.AddWithValue("note", body.Note!.Trim());
                await reject.ExecuteNonQueryAsync(ct);
            }
            await using (var items = new NpgsqlCommand(@"
                UPDATE public.borrow_request_items SET status='rejected'
                WHERE request_id=@id AND status='pending';", db, tx))
            {
                items.Parameters.AddWithValue("id", requestId);
                await items.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
            return Results.Ok(new { message = "Da tu choi don va luu ly do." });
        }
        // Prevent approval of a request submitted BEFORE a Staff ban.
        await using (var ban = new NpgsqlCommand(@"
            SELECT action FROM public.audit_logs
            WHERE entity_type='reader_borrow_access' AND entity_id=@owner
              AND action IN ('reader_borrow_block','reader_borrow_unblock')
            ORDER BY audit_id DESC LIMIT 1;", db, tx))
        {
            ban.Parameters.AddWithValue("owner", owner.ToString(CultureInfo.InvariantCulture));
            if ((await ban.ExecuteScalarAsync(ct)) as string == "reader_borrow_block")
                return Results.StatusCode(403);
        }
        var itemsToLoan = new List<(long itemId, long bookId, int days)>();
        await using (var items = new NpgsqlCommand(@"
            SELECT i.request_item_id,i.book_id,i.requested_days
            FROM public.borrow_request_items i
            JOIN public.books b ON b.book_id=i.book_id
            WHERE i.request_id=@id AND i.status='pending' AND b.is_visible=TRUE
            ORDER BY i.book_id;", db, tx))
        {
            items.Parameters.AddWithValue("id", requestId);
            await using var rr = await items.ExecuteReaderAsync(ct);
            while (await rr.ReadAsync(ct))
                itemsToLoan.Add((rr.GetInt64(0), rr.GetInt64(1), rr.GetInt32(2)));
        }
        if (itemsToLoan.Count == 0 || itemsToLoan.Count > 5)
            return Results.Conflict(new { message = "Don khong co 1-5 sach hop le." });
        await using (var total = new NpgsqlCommand(@"
            SELECT COUNT(*) FROM public.borrow_request_items WHERE request_id=@id;", db, tx))
        {
            total.Parameters.AddWithValue("id", requestId);
            if (Convert.ToInt64(await total.ExecuteScalarAsync(ct)) != itemsToLoan.Count)
                return Results.Conflict(new { message = "Co dau sach khong con hop le trong don." });
        }
        if (itemsToLoan.Any(x => x.days > maxDays))
            return Results.Conflict(new { message = $"Hang doc gia chi muon toi da {maxDays} ngay." });
        await using (var count = new NpgsqlCommand(@"
            SELECT COUNT(*) FROM public.borrow_details d JOIN public.borrows b ON b.borrow_id=d.borrow_id
            WHERE b.reader_user_id=@owner AND d.closed_at IS NULL;", db, tx))
        {
            count.Parameters.AddWithValue("owner", owner);
            var open = Convert.ToInt64(await count.ExecuteScalarAsync(ct));
            if (open + itemsToLoan.Count > maxCopies)
                return Results.Conflict(new { message = $"Reader dang muon {open} cuon; han muc {maxCopies}." });
        }
        // Lock each physical copy; no partial writes if any title has no available active copy.
        var assigned = new List<(long itemId, long copyId, int days)>();
        foreach (var item in itemsToLoan)
        {
            await using var pick = new NpgsqlCommand(@"
                SELECT c.copy_id FROM public.book_copies c
                WHERE c.book_id=@book AND c.condition_status='active'
                  AND NOT EXISTS (SELECT 1 FROM public.borrow_details d
                                  WHERE d.book_copy_id=c.copy_id AND d.closed_at IS NULL)
                ORDER BY c.copy_id LIMIT 1 FOR UPDATE OF c SKIP LOCKED;", db, tx);
            pick.Parameters.AddWithValue("book", item.bookId);
            var found = await pick.ExecuteScalarAsync(ct);
            if (found is null)
                return Results.Conflict(new { message = $"Dau sach ID {item.bookId} khong co cuon vat ly kha dung. Khong duyet don." });
            assigned.Add((item.itemId, Convert.ToInt64(found), item.days));
        }
        var at = DateTime.UtcNow;
        long borrowId;
        await using (var borrow = new NpgsqlCommand(@"
            INSERT INTO public.borrows(reader_user_id,issued_by_user_id,borrowed_at,notes)
            VALUES(@reader,@staff,@at,@note) RETURNING borrow_id;", db, tx))
        {
            borrow.Parameters.AddWithValue("reader", owner);
            borrow.Parameters.AddWithValue("staff", actor.UserId);
            borrow.Parameters.AddWithValue("at", NpgsqlDbType.TimestampTz, at);
            borrow.Parameters.AddWithValue("note", (object?)$"Don #{requestId}. {body.Note}" ?? DBNull.Value);
            borrowId = Convert.ToInt64(await borrow.ExecuteScalarAsync(ct));
        }
        foreach (var item in assigned)
        {
            var due = at.AddDays(item.days);
            await using (var detail = new NpgsqlCommand(@"
                INSERT INTO public.borrow_details
                  (borrow_id,book_copy_id,selected_loan_days,initial_due_at,due_at,
                   applied_tier_id,applied_max_loan_days,applied_max_active_copies)
                VALUES(@borrow,@copy,@days,@due,@due,@tier,@maxdays,@maxcopies);", db, tx))
            {
                detail.Parameters.AddWithValue("borrow", borrowId);
                detail.Parameters.AddWithValue("copy", item.copyId);
                detail.Parameters.AddWithValue("days", item.days);
                detail.Parameters.AddWithValue("due", NpgsqlDbType.TimestampTz, due);
                detail.Parameters.AddWithValue("tier", tierId);
                detail.Parameters.AddWithValue("maxdays", maxDays);
                detail.Parameters.AddWithValue("maxcopies", maxCopies);
                await detail.ExecuteNonQueryAsync(ct);
            }
            await using (var itemUpdate = new NpgsqlCommand(@"
                UPDATE public.borrow_request_items
                SET status='fulfilled',assigned_copy_id=@copy
                WHERE request_item_id=@id AND status='pending';", db, tx))
            {
                itemUpdate.Parameters.AddWithValue("copy", item.copyId);
                itemUpdate.Parameters.AddWithValue("id", item.itemId);
                if (await itemUpdate.ExecuteNonQueryAsync(ct) != 1)
                    throw new InvalidOperationException("Request item was concurrently changed.");
            }
        }
        await using (var done = new NpgsqlCommand(@"
            UPDATE public.borrow_requests
            SET status='fulfilled',reviewed_at=NOW(),reviewed_by_user_id=@actor,review_note=@note
            WHERE request_id=@id AND status='pending';", db, tx))
        {
            done.Parameters.AddWithValue("id", requestId);
            done.Parameters.AddWithValue("actor", actor.UserId);
            done.Parameters.AddWithValue("note", (object?)body.Note?.Trim() ?? DBNull.Value);
            if (await done.ExecuteNonQueryAsync(ct) != 1)
                throw new InvalidOperationException("Request was concurrently changed.");
        }
        await tx.CommitAsync(ct);
        return Results.Ok(new { message = $"Da duyet don va giao {assigned.Count} cuon. Phieu muon #{borrowId}.", borrowId });
    }
    catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
    { return Results.Conflict(new { message = "Mot cuon vua duoc muon. Tai lai va thu duyet." }); }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    { return Results.StatusCode(499); }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Borrow request review failed");
        return Results.Json(new { message = "Khong xu ly duoc don. Kiem tra API console." }, statusCode: 503);
    }
});

// STEP 66 — Gia hạn từng cuốn: Reader gửi yêu cầu, Staff/Admin xét duyệt.
// Sử dụng các bảng DB-11 có sẵn; không tự tạo/migrate policy hay bảng.
app.MapGet("/api/loan-renewals", async (HttpRequest request, SessionService sessions,
    IConfiguration configuration, CancellationToken ct) =>
{
    if (!app.Environment.IsDevelopment()) return Results.NotFound();
    var actor = sessions.Validate(ReadBearerToken(request));
    if (actor is null) return Results.Unauthorized();
    if (actor.Role is not ("Reader" or "Staff" or "Admin")) return Results.StatusCode(403);
    try
    {
        var cs = configuration.GetConnectionString("AivenPostgres")
            ?? throw new InvalidOperationException("Database is not configured");
        await using var db = new NpgsqlConnection(cs);
        await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            SELECT r.request_id, r.borrow_detail_id, u.username, bc.copy_code, bk.title,
                   r.status, r.requested_at, bd.due_at, COALESCE(r.review_note,''),
                   COALESCE(bd.closed_at IS NULL, FALSE)
            FROM public.borrow_renewal_requests r
            JOIN public.borrow_details bd ON bd.borrow_detail_id=r.borrow_detail_id
            JOIN public.borrows b ON b.borrow_id=bd.borrow_id
            JOIN public.users u ON u.user_id=b.reader_user_id
            JOIN public.book_copies bc ON bc.copy_id=bd.book_copy_id
            JOIN public.books bk ON bk.book_id=bc.book_id
            WHERE (@is_reader=FALSE OR b.reader_user_id=@reader)
            ORDER BY (r.status='pending') DESC, r.requested_at DESC, r.request_id DESC LIMIT 100;", db);
        cmd.Parameters.AddWithValue("is_reader", actor.Role == "Reader");
        cmd.Parameters.AddWithValue("reader", actor.UserId);
        var rows = new List<object>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            rows.Add(new
            {
                requestId = rd.GetInt64(0),
                borrowDetailId = rd.GetInt64(1),
                readerUsername = rd.GetString(2),
                copyCode = rd.GetString(3),
                bookTitle = rd.GetString(4),
                status = rd.GetString(5),
                requestedAt = rd.GetDateTime(6).ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture),
                currentDueAt = rd.GetDateTime(7).ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture),
                reviewNote = rd.GetString(8),
                isOpen = rd.GetBoolean(9)
            });
        return Results.Ok(new { requests = rows });
    }
    catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.UndefinedColumn)
    { return Results.Conflict(new { message = "Thiếu bảng/cột gia hạn DB-11. Kiểm tra schema Aiven trước khi dùng." }); }
    catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Results.StatusCode(499); }
    catch (Exception ex)
    {
        app.Logger.LogError("Load renewal requests failed: {Type}", ex.GetType().Name);
        return Results.Json(new { message = "Không tải được yêu cầu gia hạn." }, statusCode: 503);
    }
});

app.MapPost("/api/loan-renewals/request", async (NewLoanRenewalRequest input, HttpRequest request,
    SessionService sessions, IConfiguration configuration, CancellationToken ct) =>
{
    if (!app.Environment.IsDevelopment()) return Results.NotFound();
    var actor = sessions.Validate(ReadBearerToken(request));
    if (actor is null) return Results.Unauthorized();
    if (actor.Role != "Reader") return Results.StatusCode(403);
    if (input.BorrowDetailId <= 0) return Results.BadRequest(new { message = "Chọn một cuốn đang mượn." });
    try
    {
        var cs = configuration.GetConnectionString("AivenPostgres")
            ?? throw new InvalidOperationException("Database is not configured");
        await using var db = new NpgsqlConnection(cs);
        await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        DateTime due; bool open;
        // Lock the detail before reading renewal status: same order as review endpoint.
        await using (var detail = new NpgsqlCommand(@"
            SELECT d.due_at, d.closed_at IS NULL
            FROM public.borrow_details d JOIN public.borrows b ON b.borrow_id=d.borrow_id
            WHERE d.borrow_detail_id=@detail AND b.reader_user_id=@reader FOR UPDATE OF d;", db, tx))
        {
            detail.Parameters.AddWithValue("detail", input.BorrowDetailId);
            detail.Parameters.AddWithValue("reader", actor.UserId);
            await using var rd = await detail.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return Results.NotFound();
            due = rd.GetDateTime(0); open = rd.GetBoolean(1);
        }
        if (!open) return Results.Conflict(new { message = "Cuốn này đã đóng phiếu mượn." });
        int max; bool allowLate;
        await using (var policy = new NpgsqlCommand(@"
            SELECT max_renewals, allow_overdue_renewal_requests
            FROM public.borrowing_policy_versions WHERE effective_at <= NOW()
            ORDER BY effective_at DESC LIMIT 1;", db, tx))
        {
            await using var rd = await policy.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return Results.Conflict(new { message = "Chưa có chính sách gia hạn đang hiệu lực; liên hệ Admin." });
            max = rd.GetInt32(0); allowLate = rd.GetBoolean(1);
        }
        if (due < DateTime.UtcNow && !allowLate)
            return Results.Conflict(new { message = "Cuốn đã quá hạn, chính sách hiện tại không cho yêu cầu gia hạn." });
        await using (var count = new NpgsqlCommand(@"
            SELECT COUNT(*) FROM public.borrow_renewals WHERE borrow_detail_id=@detail;", db, tx))
        {
            count.Parameters.AddWithValue("detail", input.BorrowDetailId);
            if (Convert.ToInt64(await count.ExecuteScalarAsync(ct)) >= max)
                return Results.Conflict(new { message = "Cuốn đã hết số lần được gia hạn." });
        }
        await using (var pending = new NpgsqlCommand(@"
            SELECT EXISTS(SELECT 1 FROM public.borrow_renewal_requests
                         WHERE borrow_detail_id=@detail AND status='pending');", db, tx))
        {
            pending.Parameters.AddWithValue("detail", input.BorrowDetailId);
            if ((bool)(await pending.ExecuteScalarAsync(ct) ?? false))
                return Results.Conflict(new { message = "Cuốn đã có yêu cầu gia hạn đang chờ." });
        }
        long requestId;
        await using (var create = new NpgsqlCommand(@"
            INSERT INTO public.borrow_renewal_requests(borrow_detail_id,requested_by_user_id,status)
            VALUES(@detail,@reader,'pending') RETURNING request_id;", db, tx))
        {
            create.Parameters.AddWithValue("detail", input.BorrowDetailId);
            create.Parameters.AddWithValue("reader", actor.UserId);
            requestId = Convert.ToInt64(await create.ExecuteScalarAsync(ct));
        }
        await tx.CommitAsync(ct);
        return Results.Ok(new { message = $"Đã gửi yêu cầu gia hạn #{requestId}; hạn trả chưa thay đổi.", requestId });
    }
    catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
    { return Results.Conflict(new { message = "Cuốn đã có đơn gia hạn chờ duyệt." }); }
    catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.UndefinedColumn)
    { return Results.Conflict(new { message = "Thiếu bảng/cột gia hạn DB-11. Kiểm tra schema Aiven." }); }
    catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Results.StatusCode(499); }
    catch (Exception ex)
    {
        app.Logger.LogError("Create renewal request failed: {Type}", ex.GetType().Name);
        return Results.Json(new { message = "Không gửi được yêu cầu gia hạn." }, statusCode: 503);
    }
});

app.MapPost("/api/loan-renewals/{requestId:long}/review", async (long requestId,
    ReviewBorrowRequest input, HttpRequest request, SessionService sessions,
    IConfiguration configuration, CancellationToken ct) =>
{
    if (!app.Environment.IsDevelopment()) return Results.NotFound();
    var actor = sessions.Validate(ReadBearerToken(request));
    if (actor is null) return Results.Unauthorized();
    if (actor.Role is not ("Staff" or "Admin")) return Results.StatusCode(403);
    if (requestId <= 0 || (input.Note?.Length ?? 0) > 500 ||
        (!input.Approve && string.IsNullOrWhiteSpace(input.Note)))
        return Results.BadRequest(new { message = "Từ chối phải có lý do; ghi chú tối đa 500 ký tự." });
    try
    {
        var cs = configuration.GetConnectionString("AivenPostgres")
            ?? throw new InvalidOperationException("Database is not configured");
        await using var db = new NpgsqlConnection(cs);
        await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        long detailId;
        await using (var find = new NpgsqlCommand(@"
            SELECT borrow_detail_id FROM public.borrow_renewal_requests WHERE request_id=@id;", db, tx))
        {
            find.Parameters.AddWithValue("id", requestId);
            var result = await find.ExecuteScalarAsync(ct);
            if (result is null) return Results.NotFound();
            detailId = Convert.ToInt64(result);
        }
        DateTime oldDue; bool open;
        await using (var detail = new NpgsqlCommand(@"
            SELECT due_at,closed_at IS NULL FROM public.borrow_details
            WHERE borrow_detail_id=@id FOR UPDATE;", db, tx))
        {
            detail.Parameters.AddWithValue("id", detailId);
            await using var rd = await detail.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return Results.NotFound();
            oldDue = rd.GetDateTime(0); open = rd.GetBoolean(1);
        }
        await using (var pending = new NpgsqlCommand(@"
            SELECT status FROM public.borrow_renewal_requests
            WHERE request_id=@id FOR UPDATE;", db, tx))
        {
            pending.Parameters.AddWithValue("id", requestId);
            if ((await pending.ExecuteScalarAsync(ct)) as string != "pending")
                return Results.Conflict(new { message = "Yêu cầu không còn chờ duyệt." });
        }
        if (!input.Approve)
        {
            await using var reject = new NpgsqlCommand(@"
                UPDATE public.borrow_renewal_requests SET status='rejected',
                    reviewed_by_user_id=@actor,reviewed_at=NOW(),review_note=@note
                WHERE request_id=@id;", db, tx);
            reject.Parameters.AddWithValue("actor", actor.UserId);
            reject.Parameters.AddWithValue("id", requestId);
            reject.Parameters.AddWithValue("note", input.Note!.Trim());
            await reject.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Ok(new { message = "Đã từ chối yêu cầu gia hạn." });
        }
        if (!open) return Results.Conflict(new { message = "Cuốn đã trả/đóng; không thể gia hạn." });
        long policyId; int max, days; bool allowLate;
        await using (var policy = new NpgsqlCommand(@"
            SELECT policy_version_id,max_renewals,renewal_days,allow_overdue_renewal_approval
            FROM public.borrowing_policy_versions WHERE effective_at <= NOW()
            ORDER BY effective_at DESC LIMIT 1;", db, tx))
        {
            await using var rd = await policy.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return Results.Conflict(new { message = "Chưa có chính sách gia hạn hiệu lực." });
            policyId = rd.GetInt64(0); max = rd.GetInt32(1); days = rd.GetInt32(2); allowLate = rd.GetBoolean(3);
        }
        if (oldDue < DateTime.UtcNow && !allowLate)
            return Results.Conflict(new { message = "Cuốn đã quá hạn; chính sách không cho duyệt gia hạn." });
        await using (var count = new NpgsqlCommand(@"
            SELECT COUNT(*) FROM public.borrow_renewals WHERE borrow_detail_id=@id;", db, tx))
        {
            count.Parameters.AddWithValue("id", detailId);
            if (Convert.ToInt64(await count.ExecuteScalarAsync(ct)) >= max)
                return Results.Conflict(new { message = "Cuốn đã hết lượt gia hạn theo chính sách mới nhất." });
        }
        // Tính ngày ở Việt Nam (UTC+07, không DST), chốt cuối ngày 23:59:59.
        // Gia hạn bình thường từ hạn hiện tại; nếu được duyệt quá hạn thì từ hôm duyệt.
        DateTime baseUtc = oldDue > DateTime.UtcNow ? oldDue.ToUniversalTime() : DateTime.UtcNow;
        DateTime vietnamEnd = baseUtc.AddHours(7).Date.AddDays(days)
            .AddHours(23).AddMinutes(59).AddSeconds(59);
        DateTime newDue = DateTime.SpecifyKind(vietnamEnd.AddHours(-7), DateTimeKind.Utc);
        await using (var renewal = new NpgsqlCommand(@"
            INSERT INTO public.borrow_renewals(borrow_detail_id,request_id,old_due_at,new_due_at,
                renewed_by_user_id,policy_version_id)
            VALUES(@detail,@request,@old,@new,@actor,@policy);", db, tx))
        {
            renewal.Parameters.AddWithValue("detail", detailId);
            renewal.Parameters.AddWithValue("request", requestId);
            renewal.Parameters.AddWithValue("old", NpgsqlDbType.TimestampTz, oldDue.ToUniversalTime());
            renewal.Parameters.AddWithValue("new", NpgsqlDbType.TimestampTz, newDue.ToUniversalTime());
            renewal.Parameters.AddWithValue("actor", actor.UserId);
            renewal.Parameters.AddWithValue("policy", policyId);
            await renewal.ExecuteNonQueryAsync(ct);
        }
        await using (var changeDue = new NpgsqlCommand(@"
            UPDATE public.borrow_details SET due_at=@due
            WHERE borrow_detail_id=@id AND closed_at IS NULL;", db, tx))
        {
            changeDue.Parameters.AddWithValue("due", NpgsqlDbType.TimestampTz, newDue.ToUniversalTime());
            changeDue.Parameters.AddWithValue("id", detailId);
            if (await changeDue.ExecuteNonQueryAsync(ct) != 1)
                throw new InvalidOperationException("Loan closed during renewal");
        }
        await using (var approve = new NpgsqlCommand(@"
            UPDATE public.borrow_renewal_requests SET status='approved',
                reviewed_by_user_id=@actor,reviewed_at=NOW(),review_note=@note,
                applied_policy_version_id=@policy WHERE request_id=@id AND status='pending';", db, tx))
        {
            approve.Parameters.AddWithValue("actor", actor.UserId);
            approve.Parameters.AddWithValue("id", requestId);
            approve.Parameters.AddWithValue("note", (object?)input.Note?.Trim() ?? DBNull.Value);
            approve.Parameters.AddWithValue("policy", policyId);
            if (await approve.ExecuteNonQueryAsync(ct) != 1)
                throw new InvalidOperationException("Renewal request changed concurrently");
        }
        await tx.CommitAsync(ct);
        return Results.Ok(new { message = $"Đã gia hạn thêm {days} ngày. Tải lại phiếu mượn để xem hạn mới." });
    }
    catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.UndefinedColumn)
    { return Results.Conflict(new { message = "Thiếu schema gia hạn DB-11. Kiểm tra Aiven." }); }
    catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
    { return Results.Conflict(new { message = "Yêu cầu gia hạn đã được xử lý." }); }
    catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Results.StatusCode(499); }
    catch (Exception ex)
    {
        app.Logger.LogError("Review renewal failed: {Type}", ex.GetType().Name);
        return Results.Json(new { message = "Không duyệt được yêu cầu gia hạn." }, statusCode: 503);
    }
});

app.MapPost("/api/loan-renewals/{requestId:long}/cancel", async (long requestId,
    HttpRequest request, SessionService sessions, IConfiguration configuration, CancellationToken ct) =>
{
    if (!app.Environment.IsDevelopment()) return Results.NotFound();
    var actor = sessions.Validate(ReadBearerToken(request));
    if (actor is null) return Results.Unauthorized();
    if (actor.Role != "Reader") return Results.StatusCode(403);
    if (requestId <= 0) return Results.BadRequest(new { message = "Mã yêu cầu không hợp lệ." });
    try
    {
        var cs = configuration.GetConnectionString("AivenPostgres")
            ?? throw new InvalidOperationException("Database is not configured");
        await using var db = new NpgsqlConnection(cs);
        await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            UPDATE public.borrow_renewal_requests SET status='cancelled'
            WHERE request_id=@id AND requested_by_user_id=@reader AND status='pending'
            RETURNING request_id;", db);
        cmd.Parameters.AddWithValue("id", requestId);
        cmd.Parameters.AddWithValue("reader", actor.UserId);
        var result = await cmd.ExecuteScalarAsync(ct);
        if (result is null) return Results.Conflict(new { message = "Không tìm thấy yêu cầu đang chờ của bạn." });
        return Results.Ok(new { message = "Đã hủy yêu cầu gia hạn; hạn trả không thay đổi." });
    }
    catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.UndefinedColumn)
    { return Results.Conflict(new { message = "Thiếu schema gia hạn DB-11." }); }
    catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Results.StatusCode(499); }
    catch (Exception ex)
    {
        app.Logger.LogError("Cancel renewal failed: {Type}", ex.GetType().Name);
        return Results.Json(new { message = "Không hủy được yêu cầu gia hạn." }, statusCode: 503);
    }
});

app.MapPost("/api/borrow-requests/{requestId:long}/cancel", async (
    long requestId, HttpRequest request, SessionService sessions, IConfiguration config, CancellationToken ct) =>
{
    if (!app.Environment.IsDevelopment()) return Results.NotFound();
    var actor = sessions.Validate(ReadBearerToken(request));
    if (actor is null) return Results.Unauthorized();
    if (actor.Role != "Reader") return Results.StatusCode(403);
    try
    {
        var cs = config.GetConnectionString("AivenPostgres")
            ?? throw new InvalidOperationException("Database config missing.");
        await using var db = new NpgsqlConnection(cs);
        await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await using (var cmd = new NpgsqlCommand(@"
            UPDATE public.borrow_requests SET status='cancelled'
            WHERE request_id=@rid AND reader_user_id=@uid AND status='pending'
            RETURNING request_id;", db, tx))
        {
            cmd.Parameters.AddWithValue("rid", requestId);
            cmd.Parameters.AddWithValue("uid", actor.UserId);
            if (await cmd.ExecuteScalarAsync(ct) is null)
                return Results.Conflict(new { message = "Don khong ton tai, khong thuoc tai khoan hoac da xu ly." });
        }
        await using (var cmd = new NpgsqlCommand(@"
            UPDATE public.borrow_request_items SET status='cancelled'
            WHERE request_id=@rid AND status='pending';", db, tx))
        {
            cmd.Parameters.AddWithValue("rid", requestId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return Results.Ok(new { message = "Da huy don muon thanh cong." });
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Results.StatusCode(499); }
    catch (Exception ex)
    {
        app.Logger.LogError("Cancel borrow request failed: {Type}", ex.GetType().Name);
        return Results.Json(new { message = "Khong the huy don muon." }, statusCode: 503);
    }
});

app.Run();

// Du lieu JSON nhan tu WPF hoac Postman/PowerShell.
public sealed record LoginRequest(string? Username, string? Password);
public sealed record RegisterStartRequest(
    string? FullName,
    string? Username,
    string? Phone,
    string? Password,
    string? GuestClaimCode);
public sealed record GuestPreviewRequest(string? GuestClaimCode);
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

public sealed record GuestClaimIssueRequest(bool StaffVerifiedIdentity);
public sealed record ReaderBorrowAccessRequest(bool Block, string? Reason);
public sealed record CheckoutLoanRequest(string? ReaderUsername, string? CopyCode, int LoanDays);

// Vietnamese-friendly partial search: d/đ, n/N, and accented vowels compare equally.
// Applied only to public catalog data and active Reader display names on DEV localhost.
public static class VietnameseSearch
{
    public static string Fold(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        string decomposed = text.Replace('đ', 'd').Replace('Đ', 'D')
            .Normalize(NormalizationForm.FormD);
        var result = new StringBuilder(decomposed.Length);
        foreach (char ch in decomposed)
        {
            var kind = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (kind == UnicodeCategory.NonSpacingMark ||
                kind == UnicodeCategory.SpacingCombiningMark ||
                kind == UnicodeCategory.EnclosingMark) continue;
            result.Append(char.ToLowerInvariant(ch));
        }
        return result.ToString().Normalize(NormalizationForm.FormC);
    }

    // `needle` must already be Fold()-normalized.
    public static bool Matches(string? value, string needle) =>
        Fold(value).Contains(needle, StringComparison.Ordinal);
}

public sealed record NewBorrowRequest(List<NewBorrowRequestItem> Items, string? Note);
public sealed record NewBorrowRequestItem(long BookId, int RequestedDays);

public sealed record ReviewBorrowRequest(bool Approve, string? Note);

public sealed record ReaderIdentityLookupRequest(string? Phone, string? CitizenId);
public sealed record WalkInBorrowRequest(long? ExistingReaderUserId, string? FullName,
    string? Phone, string? CitizenId, bool StaffVerifiedIdentity,
    List<NewBorrowRequestItem> Items, string? Note);


public sealed record NewLoanRenewalRequest(long BorrowDetailId);
