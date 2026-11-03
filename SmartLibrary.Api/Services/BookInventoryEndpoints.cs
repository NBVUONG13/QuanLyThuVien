using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Npgsql;
using NpgsqlTypes;

namespace SmartLibrary.Api.Services;

// Step 77: catalog CRUD, physical stock, and incident handling.
// The real number of copies is always derived from book_copies; never edited as a counter.
// Development + Staff/Admin only, consistent with the current local API.
public static class BookInventoryEndpoints
{
    public sealed class BookEditRequest
    {
        public string? Title { get; set; }
        public string? Isbn { get; set; }
        public string? Description { get; set; }
        public int? PublicationYear { get; set; }
        public long? PublisherId { get; set; }
        public List<long>? AuthorIds { get; set; }
        public List<long>? CategoryIds { get; set; }
        public bool IsVisible { get; set; } = true;
    }
    public sealed class BatchStockRequest
    {
        public int Quantity { get; set; }
        public string? ShelfLocation { get; set; }
    }
    public sealed class CopyEditRequest
    {
        public string? ConditionStatus { get; set; }
        public string? ShelfLocation { get; set; }
        public string? ConditionNote { get; set; }
    }
    public sealed class IncidentRequest
    {
        public string? Kind { get; set; } // lost / damaged
        public decimal CompensationAmount { get; set; }
        public string? Note { get; set; }
    }

    public static void MapBookInventoryEndpoints(this WebApplication app)
    {
        // Lists invisible as well as visible books for stock reconciliation.
        app.MapGet("/api/inventory/books", async (HttpRequest req, SessionService sessions,
            IConfiguration config, CancellationToken ct) =>
        {
            var denied = Guard(app, req, sessions);
            if (denied is not null) return denied;
            try
            {
                await using var db = await Connect(config, ct);
                await using var cmd = new NpgsqlCommand(@"
                    SELECT b.book_id,b.title,COALESCE(b.isbn,''),COALESCE(b.publication_year,0),
                           COALESCE(p.name,''),b.is_visible,
                           (SELECT COUNT(*) FROM public.book_copies c WHERE c.book_id=b.book_id),
                           (SELECT COUNT(*) FROM public.book_copies c WHERE c.book_id=b.book_id
                              AND c.condition_status='active' AND NOT EXISTS (
                              SELECT 1 FROM public.borrow_details d WHERE d.book_copy_id=c.copy_id
                               AND d.closed_at IS NULL)),
                           (SELECT COUNT(*) FROM public.book_copies c WHERE c.book_id=b.book_id
                              AND EXISTS (SELECT 1 FROM public.borrow_details d
                                WHERE d.book_copy_id=c.copy_id AND d.closed_at IS NULL)),
                           COALESCE(b.cover_url,''),COALESCE(b.cover_object_key,'')
                    FROM public.books b LEFT JOIN public.publishers p ON p.publisher_id=b.publisher_id
                    ORDER BY b.book_id DESC LIMIT 500;", db);
                var data = new List<object>();
                await using var rd = await cmd.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct)) data.Add(new
                {
                    bookId = rd.GetInt64(0),
                    title = rd.GetString(1),
                    isbn = rd.GetString(2),
                    publicationYear = rd.GetInt32(3),
                    publisher = rd.GetString(4),
                    isVisible = rd.GetBoolean(5),
                    totalCopies = rd.GetInt64(6),
                    availableCopies = rd.GetInt64(7),
                    onLoanCopies = rd.GetInt64(8),
                    coverUrl = BookCoverUrlResolver.Resolve(rd.GetString(9), rd.GetString(10), config, app.Environment)
                });
                return Results.Ok(new { books = data });
            }
            catch (Exception ex) { return Failed(app, ex, "Không tải được danh sách kho sách."); }
        });

        app.MapGet("/api/inventory/books/{bookId:long}", async (long bookId, HttpRequest req,
            SessionService sessions, IConfiguration config, CancellationToken ct) =>
        {
            var denied = Guard(app, req, sessions);
            if (denied is not null) return denied;
            try
            {
                await using var db = await Connect(config, ct);
                await using var cmd = new NpgsqlCommand(@"
                    SELECT b.book_id,b.title,COALESCE(b.isbn,''),COALESCE(b.description,''),
                           COALESCE(b.publication_year,0),b.publisher_id,b.is_visible,
                           COALESCE((SELECT array_agg(ba.author_id ORDER BY ba.author_id)
                             FROM public.book_authors ba WHERE ba.book_id=b.book_id),'{}'::bigint[]),
                           COALESCE((SELECT array_agg(bc.category_id ORDER BY bc.category_id)
                             FROM public.book_categories bc WHERE bc.book_id=b.book_id),'{}'::bigint[]),
                           COALESCE(b.cover_url,''),COALESCE(b.cover_object_key,'')
                    FROM public.books b WHERE b.book_id=@book;", db);
                cmd.Parameters.AddWithValue("book", bookId);
                await using var rd = await cmd.ExecuteReaderAsync(ct);
                if (!await rd.ReadAsync(ct)) return Results.NotFound();
                return Results.Ok(new
                {
                    book = new
                    {
                        bookId = rd.GetInt64(0),
                        title = rd.GetString(1),
                        isbn = rd.GetString(2),
                        description = rd.GetString(3),
                        publicationYear = rd.GetInt32(4),
                        publisherId = rd.IsDBNull(5) ? (long?)null : rd.GetInt64(5),
                        isVisible = rd.GetBoolean(6),
                        authorIds = rd.GetFieldValue<long[]>(7),
                        categoryIds = rd.GetFieldValue<long[]>(8),
                        coverUrl = BookCoverUrlResolver.Resolve(rd.GetString(9), rd.GetString(10), config, app.Environment)
                    }
                });
            }
            catch (Exception ex) { return Failed(app, ex, "Không tải được thông tin đầu sách."); }
        });

        app.MapPost("/api/inventory/books", async (BookEditRequest body, HttpRequest req,
            SessionService sessions, IConfiguration config, BookEmbeddingAutoIndexer autoEmbeddings, CancellationToken ct) =>
        {
            var denied = Guard(app, req, sessions);
            if (denied is not null) return denied;
            return await SaveBook(app, body, null, req, sessions, config, autoEmbeddings, ct);
        });
        app.MapPut("/api/inventory/books/{bookId:long}", async (long bookId, BookEditRequest body,
            HttpRequest req, SessionService sessions, IConfiguration config, BookEmbeddingAutoIndexer autoEmbeddings, CancellationToken ct) =>
        {
            var denied = Guard(app, req, sessions);
            if (denied is not null) return denied;
            return await SaveBook(app, body, bookId, req, sessions, config, autoEmbeddings, ct);
        });

        // Staff/Admin upload a real cover image from WPF, not a user-supplied file path/URL.
        // The filename is generated on the server; no additional NuGet package or table is needed.
        app.MapPost("/api/inventory/books/{bookId:long}/cover", async (
            long bookId, HttpRequest req, SessionService sessions, IConfiguration config, CancellationToken ct) =>
        {
            var denied = Guard(app, req, sessions);
            if (denied is not null) return denied;

            const long maxImageBytes = 5L * 1024 * 1024;
            if (!req.HasFormContentType ||
                (req.ContentLength.HasValue && req.ContentLength.Value > maxImageBytes + 1048576))
                return Results.BadRequest(new { message = "Gửi ảnh JPG, PNG hoặc WebP, tối đa 5 MB." });

            IFormFile? cover;
            try
            {
                var form = await req.ReadFormAsync(ct);
                cover = form.Files.Count == 1 ? form.Files.GetFile("cover") : null;
            }
            catch (InvalidDataException)
            {
                return Results.BadRequest(new { message = "Dữ liệu ảnh không hợp lệ hoặc quá lớn." });
            }

            if (cover is null || cover.Length == 0 || cover.Length > maxImageBytes)
                return Results.BadRequest(new { message = "Chọn đúng một ảnh, dung lượng 1 byte đến 5 MB." });
            string ext = Path.GetExtension(cover.FileName).ToLowerInvariant();
            if (ext is not (".jpg" or ".jpeg" or ".png" or ".webp"))
                return Results.BadRequest(new { message = "Chỉ hỗ trợ ảnh .jpg, .jpeg, .png, .webp." });

            string root = Path.Combine(app.Environment.ContentRootPath, "wwwroot", "covers");
            string filename = $"book-{bookId}-{Guid.NewGuid():N}{ext}";
            string filePath = Path.Combine(root, filename);
            string relativeCoverUrl = "/covers/" + filename;
            bool committed = false;
            try
            {
                // Check book existence before writing a file; no orphan image for invalid IDs.
                await using var db = await Connect(config, ct);
                await using (var exists = new NpgsqlCommand(
                    "SELECT 1 FROM public.books WHERE book_id=@id;", db))
                {
                    exists.Parameters.AddWithValue("id", bookId);
                    if (await exists.ExecuteScalarAsync(ct) is null) return Results.NotFound();
                }

                Directory.CreateDirectory(root);
                await using (var output = new FileStream(filePath, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    await cover.CopyToAsync(output, ct);
                }
                // Match file header as well as extension. SVG/HTML/executables are never accepted.
                if (!ValidCoverHeader(filePath, ext))
                    return Results.BadRequest(new { message = "Nội dung file không khớp định dạng ảnh đã chọn." });

                await using var tx = await db.BeginTransactionAsync(ct);
                await using (var update = new NpgsqlCommand(@"
                    UPDATE public.books SET cover_url=@cover
                    WHERE book_id=@book RETURNING book_id;", db, tx))
                {
                    update.Parameters.AddWithValue("book", bookId);
                    update.Parameters.AddWithValue("cover", relativeCoverUrl);
                    if (await update.ExecuteScalarAsync(ct) is null)
                        return Results.NotFound();
                }
                await Audit(db, tx, sessions.Validate(Bearer(req))!.UserId,
                    "book_cover_updated", "books", bookId.ToString(),
                    new { coverUrl = relativeCoverUrl }, ct);
                await tx.CommitAsync(ct);
                committed = true;
                // New filename avoids stale image caches on home, search, and details.
                return Results.Ok(new
                {
                    message = "Đã cập nhật ảnh bìa sách.",
                    coverUrl = BookCoverUrlResolver.Resolve(relativeCoverUrl, null, config, app.Environment)
                });
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                app.Logger.LogWarning("Cover storage failed: {Type}", ex.GetType().Name);
                return Results.Json(new { message = "Không thể lưu ảnh. Kiểm tra quyền ghi wwwroot/covers." }, statusCode: 503);
            }
            catch (Exception ex)
            {
                return Failed(app, ex, "Không cập nhật được ảnh bìa.");
            }
            finally
            {
                // Failed requests cannot leave an unreferenced uploaded file.
                if (!committed && File.Exists(filePath))
                {
                    try { File.Delete(filePath); } catch (IOException) { }
                }
            }
        });

        // Server-generated unique copy codes; never change a physical quantity counter by hand.
        app.MapPost("/api/inventory/books/{bookId:long}/copies/batch", async (
            long bookId, BatchStockRequest body, HttpRequest req, SessionService sessions,
            IConfiguration config, CancellationToken ct) =>
        {
            var denied = Guard(app, req, sessions);
            if (denied is not null) return denied;
            if (body.Quantity < 1 || body.Quantity > 100 || (body.ShelfLocation?.Length ?? 0) > 120)
                return Results.BadRequest(new { message = "Số lượng phải từ 1 đến 100; vị trí kệ tối đa 120 ký tự." });
            try
            {
                await using var db = await Connect(config, ct);
                await using var tx = await db.BeginTransactionAsync(ct);
                await using (var exists = new NpgsqlCommand("SELECT 1 FROM public.books WHERE book_id=@id FOR UPDATE;", db, tx))
                {
                    exists.Parameters.AddWithValue("id", bookId);
                    if (await exists.ExecuteScalarAsync(ct) is null) return Results.NotFound();
                }
                var firstCode = "";
                for (var index = 0; index < body.Quantity; index++)
                {
                    // Identity is GENERATED BY DEFAULT; reserving a sequence id makes the generated barcode deterministic.
                    await using var ins = new NpgsqlCommand(@"
                        WITH next_id AS (SELECT nextval(pg_get_serial_sequence('public.book_copies','copy_id')) AS id)
                        INSERT INTO public.book_copies(copy_id,book_id,copy_code,condition_status,shelf_location,acquired_at)
                        SELECT id,@book,'BK' || @book::text || '-' || id::text,'active',@shelf,NOW()
                        FROM next_id RETURNING copy_code;", db, tx);
                    ins.Parameters.AddWithValue("book", bookId);
                    ins.Parameters.AddWithValue("shelf", (object?)body.ShelfLocation?.Trim() ?? DBNull.Value);
                    var code = (string)(await ins.ExecuteScalarAsync(ct) ?? "");
                    if (index == 0) firstCode = code;
                }
                await Audit(db, tx, sessions.Validate(Bearer(req))!.UserId, "book_copies_received", "books",
                    bookId.ToString(), new { quantity = body.Quantity, shelf = body.ShelfLocation ?? "", firstCode }, ct);
                await tx.CommitAsync(ct);
                return Results.Ok(new { message = $"Đã nhập {body.Quantity} cuốn vật lý.", quantity = body.Quantity, firstCode });
            }
            catch (Exception ex) { return Failed(app, ex, "Không nhập được kho sách."); }
        });

        app.MapGet("/api/inventory/books/{bookId:long}/copies", async (long bookId,
            HttpRequest req, SessionService sessions, IConfiguration config, CancellationToken ct) =>
        {
            var denied = Guard(app, req, sessions);
            if (denied is not null) return denied;
            try
            {
                await using var db = await Connect(config, ct);
                await using var cmd = new NpgsqlCommand(@"
                    SELECT c.copy_id,c.copy_code,c.condition_status,
                           COALESCE(c.shelf_location,''),COALESCE(c.condition_note,''),
                           (SELECT bd.borrow_detail_id FROM public.borrow_details bd
                            WHERE bd.book_copy_id=c.copy_id AND bd.closed_at IS NULL LIMIT 1)
                    FROM public.book_copies c WHERE c.book_id=@book ORDER BY c.copy_id DESC LIMIT 1000;", db);
                cmd.Parameters.AddWithValue("book", bookId);
                await using var rd = await cmd.ExecuteReaderAsync(ct);
                var data = new List<object>();
                while (await rd.ReadAsync(ct)) data.Add(new
                {
                    copyId = rd.GetInt64(0),
                    copyCode = rd.GetString(1),
                    conditionStatus = rd.GetString(2),
                    shelfLocation = rd.GetString(3),
                    conditionNote = rd.GetString(4),
                    openBorrowDetailId = rd.IsDBNull(5) ? (long?)null : rd.GetInt64(5),
                    onLoan = !rd.IsDBNull(5)
                });
                return Results.Ok(new { copies = data });
            }
            catch (Exception ex) { return Failed(app, ex, "Không tải được danh sách bản sao."); }
        });

        // Only off-loan copies may be edited here; an on-loan incident must close the loan atomically.
        app.MapPut("/api/inventory/copies/{copyId:long}", async (long copyId,
            CopyEditRequest body, HttpRequest req, SessionService sessions,
            IConfiguration config, CancellationToken ct) =>
        {
            var denied = Guard(app, req, sessions);
            if (denied is not null) return denied;
            string status = (body.ConditionStatus ?? "").Trim().ToLowerInvariant();
            if (status is not ("active" or "damaged" or "lost" or "retired") ||
                (body.ShelfLocation?.Length ?? 0) > 120 || (body.ConditionNote?.Length ?? 0) > 1000)
                return Results.BadRequest(new { message = "Tình trạng không hợp lệ, ghi chú ≤1000 ký tự, kệ ≤120 ký tự." });
            try
            {
                await using var db = await Connect(config, ct);
                await using var tx = await db.BeginTransactionAsync(ct);
                await using (var hold = new NpgsqlCommand("SELECT 1 FROM public.book_copies WHERE copy_id=@id FOR UPDATE;", db, tx))
                {
                    hold.Parameters.AddWithValue("id", copyId);
                    if (await hold.ExecuteScalarAsync(ct) is null) return Results.NotFound();
                }
                await using (var active = new NpgsqlCommand(@"
                    SELECT EXISTS(SELECT 1 FROM public.borrow_details WHERE book_copy_id=@id AND closed_at IS NULL);", db, tx))
                {
                    active.Parameters.AddWithValue("id", copyId);
                    if ((bool)(await active.ExecuteScalarAsync(ct) ?? false))
                        return Results.Conflict(new { message = "Cuốn đang được mượn. Dùng Báo mất / Báo hỏng ở phiếu đang mượn." });
                }
                await using (var update = new NpgsqlCommand(@"
                    UPDATE public.book_copies SET condition_status=@status,shelf_location=@shelf,condition_note=@note
                    WHERE copy_id=@id;", db, tx))
                {
                    update.Parameters.AddWithValue("id", copyId);
                    update.Parameters.AddWithValue("status", status);
                    update.Parameters.AddWithValue("shelf", (object?)body.ShelfLocation?.Trim() ?? DBNull.Value);
                    update.Parameters.AddWithValue("note", (object?)body.ConditionNote?.Trim() ?? DBNull.Value);
                    await update.ExecuteNonQueryAsync(ct);
                }
                await Audit(db, tx, sessions.Validate(Bearer(req))!.UserId, "book_copy_updated", "book_copies",
                    copyId.ToString(), new { status, shelf = body.ShelfLocation ?? "", note = body.ConditionNote ?? "" }, ct);
                await tx.CommitAsync(ct);
                return Results.Ok(new { message = "Đã cập nhật tình trạng và vị trí bản sao." });
            }
            catch (Exception ex) { return Failed(app, ex, "Không sửa được bản sao."); }
        });

        // Lost book: close_reason=lost, returned_at=NULL. Damaged return: close_reason=returned.
        // The copy status, overdue assessment, compensation fine and audit are atomic.
        app.MapPost("/api/inventory/loans/{borrowDetailId:long}/incident", async (long borrowDetailId,
            IncidentRequest body, HttpRequest req, SessionService sessions,
            IConfiguration config, CancellationToken ct) =>
        {
            var denied = Guard(app, req, sessions);
            if (denied is not null) return denied;
            string kind = (body.Kind ?? "").Trim().ToLowerInvariant();
            string note = body.Note?.Trim() ?? "";
            if (kind is not ("lost" or "damaged") ||
                body.CompensationAmount < 0 || body.CompensationAmount > 100000000m ||
                note.Length < 8 || note.Length > 1000)
                return Results.BadRequest(new { message = "Chọn mất/hỏng, mức bồi thường 0–100 triệu, lý do 8–1000 ký tự." });
            try
            {
                await using var db = await Connect(config, ct);
                await using var tx = await db.BeginTransactionAsync(ct);
                long copyId, readerId;
                int lateDays;
                // Locks protect against concurrent return/incident and keep counts correct.
                await using (var q = new NpgsqlCommand(@"
                    SELECT bd.book_copy_id,b.reader_user_id,
                       GREATEST(0,(NOW() AT TIME ZONE 'Asia/Ho_Chi_Minh')::date -
                         (bd.due_at AT TIME ZONE 'Asia/Ho_Chi_Minh')::date),
                       bd.closed_at IS NULL
                    FROM public.borrow_details bd JOIN public.borrows b ON b.borrow_id=bd.borrow_id
                    WHERE bd.borrow_detail_id=@id FOR UPDATE OF bd;", db, tx))
                {
                    q.Parameters.AddWithValue("id", borrowDetailId);
                    await using var rd = await q.ExecuteReaderAsync(ct);
                    if (!await rd.ReadAsync(ct)) return Results.NotFound();
                    copyId = rd.GetInt64(0); readerId = rd.GetInt64(1);
                    lateDays = rd.GetInt32(2);
                    if (!rd.GetBoolean(3)) return Results.Conflict(new { message = "Phiếu mượn đã đóng. Không ghi sự cố hai lần." });
                }
                await using (var hold = new NpgsqlCommand("SELECT 1 FROM public.book_copies WHERE copy_id=@id FOR UPDATE;", db, tx))
                {
                    hold.Parameters.AddWithValue("id", copyId);
                    await hold.ExecuteScalarAsync(ct);
                }
                decimal overdueAmount = 0;
                long? policyId = null;
                if (lateDays > 0)
                {
                    await using (var policy = new NpgsqlCommand(@"
                        SELECT policy_version_id,overdue_grace_days,overdue_fee_per_day,max_overdue_fee_per_detail
                        FROM public.borrowing_policy_versions WHERE effective_at<=NOW()
                        ORDER BY effective_at DESC LIMIT 1;", db, tx))
                    {
                        await using var rd = await policy.ExecuteReaderAsync(ct);
                        if (!await rd.ReadAsync(ct))
                            return Results.Conflict(new { message = "Thiếu chính sách quá hạn. Liên hệ Admin." });
                        policyId = rd.GetInt64(0);
                        overdueAmount = Math.Min(rd.GetDecimal(3), rd.GetDecimal(2) * Math.Max(0, lateDays - rd.GetInt32(1)));
                    }
                }
                if (overdueAmount > 0)
                {
                    await using var check = new NpgsqlCommand(@"
                        SELECT 1 FROM public.fines WHERE borrow_detail_id=@id AND fine_type='overdue' LIMIT 1;", db, tx);
                    check.Parameters.AddWithValue("id", borrowDetailId);
                    if (await check.ExecuteScalarAsync(ct) is null)
                        await InsertFine(db, tx, borrowDetailId, "overdue", overdueAmount, policyId,
                            sessions.Validate(Bearer(req))!.UserId, $"Quá hạn {lateDays} ngày; ghi nhận khi báo {kind}.", ct);
                    else overdueAmount = 0;
                }
                if (body.CompensationAmount > 0)
                {
                    await using var check = new NpgsqlCommand(@"
                        SELECT 1 FROM public.fines WHERE borrow_detail_id=@id AND fine_type=@kind LIMIT 1;", db, tx);
                    check.Parameters.AddWithValue("id", borrowDetailId);
                    check.Parameters.AddWithValue("kind", kind);
                    if (await check.ExecuteScalarAsync(ct) is not null)
                        return Results.Conflict(new { message = "Sự cố này đã có khoản bồi thường." });
                    await InsertFine(db, tx, borrowDetailId, kind, body.CompensationAmount, null,
                        sessions.Validate(Bearer(req))!.UserId, note, ct);
                }
                await using (var close = new NpgsqlCommand(@"
                    UPDATE public.borrow_details
                    SET closed_at=NOW(), returned_at=CASE WHEN @kind='damaged' THEN NOW() ELSE NULL END,
                        close_reason=CASE WHEN @kind='damaged' THEN 'returned' ELSE 'lost' END
                    WHERE borrow_detail_id=@id AND closed_at IS NULL;", db, tx))
                {
                    close.Parameters.AddWithValue("id", borrowDetailId);
                    close.Parameters.AddWithValue("kind", kind);
                    if (await close.ExecuteNonQueryAsync(ct) != 1)
                        return Results.Conflict(new { message = "Phiếu đã được đóng bởi thao tác khác." });
                }
                await using (var changeCopy = new NpgsqlCommand(@"
                    UPDATE public.book_copies SET condition_status=@kind,condition_note=@note
                    WHERE copy_id=@copy;", db, tx))
                {
                    changeCopy.Parameters.AddWithValue("copy", copyId);
                    changeCopy.Parameters.AddWithValue("kind", kind);
                    changeCopy.Parameters.AddWithValue("note", note);
                    await changeCopy.ExecuteNonQueryAsync(ct);
                }
                await using (var notification = new NpgsqlCommand(@"
                    INSERT INTO public.notifications(user_id,notification_type,title,body)
                    VALUES(@user,'fine_assessed',@title,@body);", db, tx))
                {
                    notification.Parameters.AddWithValue("user", readerId);
                    notification.Parameters.AddWithValue("title", kind == "lost" ? "Ghi nhận mất sách" : "Ghi nhận trả sách hỏng");
                    notification.Parameters.AddWithValue("body", $"Thủ thư đã ghi nhận {(kind == "lost" ? "mất" : "hỏng")} cuốn sách; bồi thường {body.CompensationAmount:0} VND, phạt quá hạn mới {overdueAmount:0} VND. Xem mục tiền phạt.");
                    await notification.ExecuteNonQueryAsync(ct);
                }
                await Audit(db, tx, sessions.Validate(Bearer(req))!.UserId, "borrow_copy_incident", "borrow_details",
                    borrowDetailId.ToString(), new { kind, copyId, compensation = body.CompensationAmount, overdueAmount, note }, ct);
                await tx.CommitAsync(ct);
                return Results.Ok(new
                {
                    message = kind == "lost" ? "Đã đóng phiếu báo mất và cập nhật kho." : "Đã nhận trả sách hỏng và cập nhật kho.",
                    borrowDetailId,
                    compensationAmount = body.CompensationAmount,
                    assessedOverdue = overdueAmount
                });
            }
            catch (Exception ex) { return Failed(app, ex, "Không xử lý được sự cố sách."); }
        });
    }

    private static async Task<IResult> SaveBook(WebApplication app, BookEditRequest body, long? bookId,
        HttpRequest req, SessionService sessions, IConfiguration config, BookEmbeddingAutoIndexer autoEmbeddings, CancellationToken ct)
    {
        string title = (body.Title ?? "").Trim(), isbn = (body.Isbn ?? "").Trim(),
               description = (body.Description ?? "").Trim();
        if (title.Length < 2 || title.Length > 300 || isbn.Length > 20 ||
            description.Length > 10000 || (body.PublicationYear.HasValue &&
              (body.PublicationYear < 0 || body.PublicationYear > 3000)) ||
            (body.PublisherId.HasValue && body.PublisherId <= 0) ||
            (body.AuthorIds?.Count ?? 0) > 30 || (body.CategoryIds?.Count ?? 0) > 30 ||
            (body.AuthorIds?.Any(x => x <= 0) ?? false) || (body.CategoryIds?.Any(x => x <= 0) ?? false))
            return Results.BadRequest(new { message = "Tên 2–300 ký tự; ISBN tối đa 20, mô tả tối đa 10000; kiểm tra NXB/tác giả/thể loại." });
        try
        {
            await using var db = await Connect(config, ct);
            await using var tx = await db.BeginTransactionAsync(ct);
            long id;
            if (bookId.HasValue)
            {
                await using var update = new NpgsqlCommand(@"
                    UPDATE public.books SET title=@title,isbn=@isbn,description=@desc,
                        publication_year=@year,publisher_id=@publisher,is_visible=@visible
                    WHERE book_id=@id RETURNING book_id;", db, tx);
                update.Parameters.AddWithValue("id", bookId.Value);
                SetBookParams(update, body, title, isbn, description);
                var value = await update.ExecuteScalarAsync(ct);
                if (value is null) return Results.NotFound();
                id = Convert.ToInt64(value);
            }
            else
            {
                await using var insert = new NpgsqlCommand(@"
                    INSERT INTO public.books(title,isbn,description,publication_year,publisher_id,is_visible)
                    VALUES(@title,@isbn,@desc,@year,@publisher,@visible) RETURNING book_id;", db, tx);
                SetBookParams(insert, body, title, isbn, description);
                id = Convert.ToInt64(await insert.ExecuteScalarAsync(ct));
            }
            await using (var clear = new NpgsqlCommand(@"
                DELETE FROM public.book_authors WHERE book_id=@id;
                DELETE FROM public.book_categories WHERE book_id=@id;", db, tx))
            {
                clear.Parameters.AddWithValue("id", id);
                await clear.ExecuteNonQueryAsync(ct);
            }
            foreach (var authorId in (body.AuthorIds ?? new List<long>()).Distinct())
            {
                await using var add = new NpgsqlCommand(@"
                    INSERT INTO public.book_authors(book_id,author_id) VALUES(@book,@ref);", db, tx);
                add.Parameters.AddWithValue("book", id);
                add.Parameters.AddWithValue("ref", authorId);
                await add.ExecuteNonQueryAsync(ct);
            }
            foreach (var categoryId in (body.CategoryIds ?? new List<long>()).Distinct())
            {
                await using var add = new NpgsqlCommand(@"
                    INSERT INTO public.book_categories(book_id,category_id) VALUES(@book,@ref);", db, tx);
                add.Parameters.AddWithValue("book", id);
                add.Parameters.AddWithValue("ref", categoryId);
                await add.ExecuteNonQueryAsync(ct);
            }
            await Audit(db, tx, sessions.Validate(Bearer(req))!.UserId,
                bookId.HasValue ? "book_updated" : "book_created", "books", id.ToString(),
                new { title, isbn, body.IsVisible, body.PublisherId, body.AuthorIds, body.CategoryIds }, ct);
            await tx.CommitAsync(ct);
            // The transaction is committed first. Enqueue a non-blocking background scan;
            // a failed/offline embedding model never rolls back the saved book.
            autoEmbeddings.RequestScan();
            return Results.Ok(new { message = bookId.HasValue ? "Đã lưu thông tin đầu sách." : "Đã tạo đầu sách mới.", bookId = id });
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        { return Results.Conflict(new { message = "ISBN đã có ở một đầu sách khác." }); }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        { return Results.BadRequest(new { message = "NXB/tác giả/thể loại không tồn tại. Tải lại danh mục." }); }
        catch (Exception ex) { return Failed(app, ex, "Không lưu được đầu sách."); }
    }

    private static bool ValidCoverHeader(string path, string extension)
    {
        Span<byte> header = stackalloc byte[12];
        using var stream = File.OpenRead(path);
        if (stream.Read(header) < header.Length) return false;
        if (extension is ".jpg" or ".jpeg")
            return header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF;
        if (extension == ".png")
            return header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        if (extension == ".webp")
            return header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8);
        return false;
    }

    private static void SetBookParams(NpgsqlCommand cmd, BookEditRequest body,
        string title, string isbn, string description)
    {
        cmd.Parameters.AddWithValue("title", title);
        cmd.Parameters.Add("isbn", NpgsqlDbType.Varchar).Value = isbn.Length == 0 ? DBNull.Value : isbn;
        cmd.Parameters.Add("desc", NpgsqlDbType.Text).Value = description.Length == 0 ? DBNull.Value : description;
        cmd.Parameters.Add("year", NpgsqlDbType.Integer).Value = (object?)body.PublicationYear ?? DBNull.Value;
        cmd.Parameters.Add("publisher", NpgsqlDbType.Bigint).Value = (object?)body.PublisherId ?? DBNull.Value;
        cmd.Parameters.AddWithValue("visible", body.IsVisible);
    }
    private static async Task<long> InsertFine(NpgsqlConnection db, NpgsqlTransaction tx,
        long detail, string kind, decimal amount, long? policyId, long actorId, string note, CancellationToken ct)
    {
        await using var fine = new NpgsqlCommand(@"
            INSERT INTO public.fines(borrow_detail_id,fine_type,amount,waived_amount,
              assessment_policy_version_id,assessed_by_user_id,note)
            VALUES(@id,@kind,@amount,0,@policy,@actor,@note) RETURNING fine_id;", db, tx);
        fine.Parameters.AddWithValue("id", detail);
        fine.Parameters.AddWithValue("kind", kind);
        fine.Parameters.AddWithValue("amount", amount);
        fine.Parameters.Add("policy", NpgsqlDbType.Bigint).Value = (object?)policyId ?? DBNull.Value;
        fine.Parameters.AddWithValue("actor", actorId);
        fine.Parameters.AddWithValue("note", note);
        return Convert.ToInt64(await fine.ExecuteScalarAsync(ct));
    }
    private static async Task Audit(NpgsqlConnection db, NpgsqlTransaction tx, long actorId,
        string action, string entity, string id, object details, CancellationToken ct)
    {
        await using var log = new NpgsqlCommand(@"
            INSERT INTO public.audit_logs(actor_user_id,action,entity_type,entity_id,details)
            VALUES(@actor,@action,@entity,@id,CAST(@details AS jsonb));", db, tx);
        log.Parameters.AddWithValue("actor", actorId);
        log.Parameters.AddWithValue("action", action);
        log.Parameters.AddWithValue("entity", entity);
        log.Parameters.AddWithValue("id", id);
        log.Parameters.AddWithValue("details", JsonSerializer.Serialize(details));
        await log.ExecuteNonQueryAsync(ct);
    }
    private static async Task<NpgsqlConnection> Connect(IConfiguration config, CancellationToken ct)
    {
        var cs = config.GetConnectionString("AivenPostgres") ??
            throw new InvalidOperationException("AivenPostgres missing.");
        var db = new NpgsqlConnection(cs);
        try { await db.OpenAsync(ct); return db; }
        catch { await db.DisposeAsync(); throw; }
    }
    private static IResult? Guard(WebApplication app, HttpRequest req, SessionService sessions)
    {
        if (!app.Environment.IsDevelopment()) return Results.NotFound();
        var actor = sessions.Validate(Bearer(req));
        if (actor is null) return Results.Unauthorized();
        if (actor.Role is not ("Admin" or "Staff")) return Results.StatusCode(403);
        return null;
    }
    private static string? Bearer(HttpRequest req)
    {
        string value = req.Headers.Authorization.ToString();
        if (!value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        var token = value.Substring(7).Trim();
        return token.Length is > 0 and <= 200 ? token : null;
    }
    private static IResult Failed(WebApplication app, Exception ex, string message)
    {
        app.Logger.LogError("Inventory operation failed: {Type}", ex.GetType().Name);
        return Results.Json(new { message }, statusCode: 503);
    }
}
