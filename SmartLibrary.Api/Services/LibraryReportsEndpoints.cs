using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Npgsql;
using NpgsqlTypes;

namespace SmartLibrary.Api.Services;

// Step 81: read-only library reports. No migration, no change to loans or existing dashboard.
// Local Development like the current API; strictly Admin-only on the server.
public static class LibraryReportsEndpoints
{
    public static void MapLibraryReportsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/admin/library-reports", async (HttpRequest request,
            SessionService sessions, IConfiguration config, CancellationToken ct) =>
        {
            if (!ProductionAccess.IsEnabled(app)) return Results.NotFound();
            var actor = sessions.Validate(Bearer(request));
            if (actor is null) return Results.Unauthorized();
            if (actor.Role != "Admin") return Results.StatusCode(403);

            var textFrom = request.Query["from"].ToString();
            var textTo = request.Query["to"].ToString();
            var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7));
            var first = new DateOnly(today.Year, today.Month, 1);
            if (string.IsNullOrWhiteSpace(textFrom)) textFrom = first.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(textTo)) textTo = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (!DateOnly.TryParseExact(textFrom, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from) ||
                !DateOnly.TryParseExact(textTo, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var to) ||
                from.Year < 2000 || to.Year > 2100 || to < from || to.DayNumber - from.DayNumber > 365)
                return Results.BadRequest(new { message = "Khoảng ngày không hợp lệ. Chọn YYYY-MM-DD, tối đa 366 ngày." });

            // Boundaries are midnight in Vietnam; upper bound is exclusive.
            var fromUtc = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(7)).UtcDateTime;
            var untilUtc = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(7)).UtcDateTime;
            try
            {
                var connectionString = config.GetConnectionString("AivenPostgres") ??
                    throw new InvalidOperationException("Missing AivenPostgres connection string.");
                await using var db = new NpgsqlConnection(connectionString);
                await db.OpenAsync(ct);
                // One consistent read-only snapshot for every section.
                await using var tx = await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
                await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", db, tx))
                    await readOnly.ExecuteNonQueryAsync(ct);

                long borrowOrders, borrowedCopies, returnedCopies, overdueNow, visibleBooks, availableCopies;
                decimal assessedFines, collectedFines, outstandingFines;
                await using (var cmd = Command(db, tx, fromUtc, untilUtc, @"
                    SELECT
                    (SELECT COUNT(*) FROM public.borrows b WHERE b.borrowed_at >= @fromUtc AND b.borrowed_at < @untilUtc),
                    (SELECT COUNT(*) FROM public.borrow_details d JOIN public.borrows b ON b.borrow_id=d.borrow_id
                      WHERE b.borrowed_at >= @fromUtc AND b.borrowed_at < @untilUtc),
                    (SELECT COUNT(*) FROM public.borrow_details d WHERE d.close_reason='returned'
                      AND d.closed_at >= @fromUtc AND d.closed_at < @untilUtc),
                    (SELECT COUNT(*) FROM public.borrow_details d WHERE d.closed_at IS NULL AND d.due_at < now()),
                    (SELECT COALESCE(SUM(f.amount),0) FROM public.fines f
                      WHERE f.assessed_at >= @fromUtc AND f.assessed_at < @untilUtc),
                    (SELECT COALESCE(SUM(p.amount),0) FROM public.fine_payments p
                      WHERE p.paid_at >= @fromUtc AND p.paid_at < @untilUtc),
                    (SELECT COUNT(*) FROM public.books WHERE is_visible=TRUE),
                    (SELECT COUNT(*) FROM public.book_copies c WHERE c.condition_status='active'
                      AND NOT EXISTS (SELECT 1 FROM public.borrow_details d WHERE d.book_copy_id=c.copy_id AND d.closed_at IS NULL)),
                    (SELECT COALESCE(SUM(GREATEST(0, f.amount - f.waived_amount -
                      COALESCE((SELECT SUM(p.amount) FROM public.fine_payments p WHERE p.fine_id=f.fine_id),0))),0)
                      FROM public.fines f);"))
                await using (var rd = await cmd.ExecuteReaderAsync(ct))
                {
                    if (!await rd.ReadAsync(ct)) throw new InvalidOperationException("Missing report summary row");
                    borrowOrders = rd.GetInt64(0); borrowedCopies = rd.GetInt64(1);
                    returnedCopies = rd.GetInt64(2); overdueNow = rd.GetInt64(3);
                    assessedFines = rd.GetDecimal(4); collectedFines = rd.GetDecimal(5);
                    visibleBooks = rd.GetInt64(6); availableCopies = rd.GetInt64(7);
                    outstandingFines = rd.GetDecimal(8);
                }

                var topBooks = new List<object>();
                await using (var cmd = Command(db, tx, fromUtc, untilUtc, @"
                    SELECT bk.book_id, bk.title, COUNT(*) AS copies, COUNT(DISTINCT b.reader_user_id) AS readers
                    FROM public.borrow_details d JOIN public.borrows b ON b.borrow_id=d.borrow_id
                    JOIN public.book_copies c ON c.copy_id=d.book_copy_id
                    JOIN public.books bk ON bk.book_id=c.book_id
                    WHERE b.borrowed_at >= @fromUtc AND b.borrowed_at < @untilUtc
                    GROUP BY bk.book_id, bk.title
                    ORDER BY copies DESC, bk.title ASC LIMIT 20;"))
                await using (var rd = await cmd.ExecuteReaderAsync(ct))
                    while (await rd.ReadAsync(ct)) topBooks.Add(new
                    {
                        bookId = rd.GetInt64(0),
                        title = rd.GetString(1),
                        copies = rd.GetInt64(2),
                        readers = rd.GetInt64(3)
                    });

                var daily = new List<object>();
                await using (var cmd = Command(db, tx, fromUtc, untilUtc, @"
                    SELECT to_char((b.borrowed_at AT TIME ZONE 'Asia/Ho_Chi_Minh')::date,'DD/MM/YYYY') AS day,
                           COUNT(DISTINCT b.borrow_id) AS orders, COUNT(d.borrow_detail_id) AS copies
                    FROM public.borrows b LEFT JOIN public.borrow_details d ON d.borrow_id=b.borrow_id
                    WHERE b.borrowed_at >= @fromUtc AND b.borrowed_at < @untilUtc
                    GROUP BY (b.borrowed_at AT TIME ZONE 'Asia/Ho_Chi_Minh')::date
                    ORDER BY (b.borrowed_at AT TIME ZONE 'Asia/Ho_Chi_Minh')::date DESC LIMIT 366;"))
                await using (var rd = await cmd.ExecuteReaderAsync(ct))
                    while (await rd.ReadAsync(ct)) daily.Add(new
                    {
                        day = rd.GetString(0),
                        orders = rd.GetInt64(1),
                        copies = rd.GetInt64(2)
                    });

                // Current overdue list is intentionally independent of the date filter.
                var overdue = new List<object>();
                await using (var cmd = new NpgsqlCommand(@"
                    SELECT rp.reader_code, bk.title, c.copy_code,
                      to_char(d.due_at AT TIME ZONE 'Asia/Ho_Chi_Minh','DD/MM/YYYY HH24:MI'),
                      GREATEST(1, CEIL(EXTRACT(EPOCH FROM now()-d.due_at) / 86400)::int)
                    FROM public.borrow_details d
                    JOIN public.borrows b ON b.borrow_id=d.borrow_id
                    JOIN public.reader_profiles rp ON rp.user_id=b.reader_user_id
                    JOIN public.book_copies c ON c.copy_id=d.book_copy_id
                    JOIN public.books bk ON bk.book_id=c.book_id
                    WHERE d.closed_at IS NULL AND d.due_at < now()
                    ORDER BY d.due_at ASC LIMIT 100;", db, tx))
                await using (var rd = await cmd.ExecuteReaderAsync(ct))
                    while (await rd.ReadAsync(ct)) overdue.Add(new
                    {
                        readerCode = rd.GetString(0),
                        title = rd.GetString(1),
                        copyCode = rd.GetString(2),
                        dueAt = rd.GetString(3),
                        daysLate = rd.GetInt32(4)
                    });

                var fineBreakdown = new List<object>();
                await using (var cmd = Command(db, tx, fromUtc, untilUtc, @"
                    WITH assessed AS (
                      SELECT f.fine_type, COUNT(*) AS items, SUM(f.amount) AS amount
                      FROM public.fines f WHERE f.assessed_at >= @fromUtc AND f.assessed_at < @untilUtc
                      GROUP BY f.fine_type
                    ), paid AS (
                      SELECT f.fine_type, SUM(p.amount) AS amount
                      FROM public.fine_payments p JOIN public.fines f ON f.fine_id=p.fine_id
                      WHERE p.paid_at >= @fromUtc AND p.paid_at < @untilUtc
                      GROUP BY f.fine_type
                    )
                    SELECT kinds.kind, COALESCE(a.items,0), COALESCE(a.amount,0), COALESCE(p.amount,0)
                    FROM (VALUES ('overdue'),('lost'),('damaged')) AS kinds(kind)
                    LEFT JOIN assessed a ON a.fine_type=kinds.kind
                    LEFT JOIN paid p ON p.fine_type=kinds.kind
                    ORDER BY CASE kinds.kind WHEN 'overdue' THEN 1 WHEN 'lost' THEN 2 ELSE 3 END;"))
                await using (var rd = await cmd.ExecuteReaderAsync(ct))
                    while (await rd.ReadAsync(ct)) fineBreakdown.Add(new
                    {
                        fineType = rd.GetString(0),
                        count = rd.GetInt64(1),
                        assessed = rd.GetDecimal(2),
                        collected = rd.GetDecimal(3)
                    });

                await tx.CommitAsync(ct);
                return Results.Ok(new
                {
                    from = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    to = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    summary = new
                    {
                        borrowOrders,
                        borrowedCopies,
                        returnedCopies,
                        overdueNow,
                        visibleBooks,
                        availableCopies,
                        assessedFines,
                        collectedFines,
                        outstandingFines
                    },
                    topBooks,
                    daily,
                    overdue,
                    fineBreakdown,
                    note = "Số quá hạn, sách còn sẵn và phạt chưa thanh toán là số tại thời điểm xem, không phụ thuộc khoảng ngày. Các chỉ số còn lại theo khoảng ngày đã chọn. Danh sách quá hạn tối đa 100 cuốn."
                });
            }
            catch (Exception ex)
            {
                app.Logger.LogError("Library reports failed ({Type})", ex.GetType().Name);
                return Results.Json(new { message = "Không tải được báo cáo. Kiểm tra kết nối và cấu trúc database." }, statusCode: 503);
            }
        });
    }

    private static NpgsqlCommand Command(NpgsqlConnection db, NpgsqlTransaction tx, DateTime fromUtc, DateTime untilUtc, string sql)
    {
        var cmd = new NpgsqlCommand(sql, db, tx);
        cmd.Parameters.Add("fromUtc", NpgsqlDbType.TimestampTz).Value = fromUtc;
        cmd.Parameters.Add("untilUtc", NpgsqlDbType.TimestampTz).Value = untilUtc;
        return cmd;
    }
    private static string? Bearer(HttpRequest request)
    {
        string value = request.Headers.Authorization.ToString();
        if (!value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        string token = value.Substring(7).Trim();
        return token.Length is > 0 and <= 200 ? token : null;
    }
}
