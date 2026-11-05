using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace SmartLibrary.Api.Services;

// Step 85 - Development-only, authenticated Reader reminders.
// Uses existing borrows, borrow_details, book_copies, books and notifications.
// No new table, no timer running on the server, no SMS or external service.
public static class LoanDueReminderEndpoints
{
    public static void MapLoanDueReminderEndpoints(this WebApplication app)
    {
        app.MapPost("/api/notifications/sync-due", async (
            HttpRequest request, SessionService sessions, IConfiguration configuration,
            CancellationToken ct) =>
        {
            if (!ProductionAccess.IsEnabled(app)) return Results.NotFound();
            var actor = sessions.Validate(Bearer(request));
            if (actor is null) return Results.Unauthorized();
            if (actor.Role != "Reader") return Results.StatusCode(StatusCodes.Status403Forbidden);

            try
            {
                string cs = configuration.GetConnectionString("AivenPostgres")
                    ?? throw new InvalidOperationException("AivenPostgres is not configured.");
                await using var db = new NpgsqlConnection(cs);
                await db.OpenAsync(ct);
                await using var tx = await db.BeginTransactionAsync(ct);

                // Serializes concurrent sync requests from the same Reader, including
                // requests coming from two app windows/processes. No schema migration needed.
                await using (var guard = new NpgsqlCommand(@"
                    SELECT pg_advisory_xact_lock(
                        hashtextextended('smart-library:due-reminders:' || CAST(@reader AS text), 0));",
                    db, tx))
                {
                    guard.Parameters.AddWithValue("reader", actor.UserId);
                    await guard.ExecuteNonQueryAsync(ct);
                }

                // Dates are compared as local Vietnam calendar days. An event key
                // is unique per physical loan detail + deadline + threshold. Repeated
                // refreshes never generate the same reminder twice.
                // Also covers 2 days remaining if the Reader missed the 3-day check.
                const string sql = @"
                    WITH active AS (
                        SELECT d.borrow_detail_id, b.title,
                            d.due_at AT TIME ZONE 'Asia/Ho_Chi_Minh' AS local_due,
                            (d.due_at AT TIME ZONE 'Asia/Ho_Chi_Minh')::date AS due_date,
                            ((d.due_at AT TIME ZONE 'Asia/Ho_Chi_Minh')::date
                              - (NOW() AT TIME ZONE 'Asia/Ho_Chi_Minh')::date) AS days_left
                        FROM public.borrow_details d
                        JOIN public.borrows br ON br.borrow_id=d.borrow_id
                        JOIN public.book_copies c ON c.copy_id=d.book_copy_id
                        JOIN public.books b ON b.book_id=c.book_id
                        WHERE br.reader_user_id=@reader AND d.closed_at IS NULL
                    ), due AS (
                        SELECT active.*,
                            CASE WHEN days_left < 0 THEN 'overdue'
                                 WHEN days_left = 0 THEN 'today'
                                 WHEN days_left = 1 THEN '1d'
                                 WHEN days_left IN (2, 3) THEN '3d'
                                 ELSE NULL END AS stage
                        FROM active
                    ), events AS (
                        SELECT 'loan_due_' || stage || ':' || borrow_detail_id::text ||
                                   ':' || TO_CHAR(due_date,'YYYYMMDD') AS event_key,
                               CASE stage WHEN 'overdue' THEN 'Sách đã quá hạn trả'
                                          WHEN 'today' THEN 'Sách đến hạn trả hôm nay'
                                          WHEN '1d' THEN 'Sách còn 1 ngày đến hạn'
                                          ELSE 'Sách sắp đến hạn trả' END AS title,
                               'Sách ""' || LEFT(title, 160) || '"" — hạn trả ' ||
                                   TO_CHAR(local_due,'DD/MM/YYYY HH24:MI') || ' (VN). ' ||
                                   CASE WHEN days_left < 0
                                             THEN 'Đã quá hạn ' || ABS(days_left)::text || ' ngày.'
                                        WHEN days_left = 0 THEN 'Hôm nay là ngày đến hạn.'
                                        ELSE 'Còn ' || days_left::text || ' ngày.' END
                                   AS body
                        FROM due WHERE stage IS NOT NULL
                    )
                    INSERT INTO public.notifications(user_id, notification_type, title, body)
                    SELECT @reader, e.event_key, e.title, e.body
                    FROM events e
                    WHERE NOT EXISTS (
                        SELECT 1 FROM public.notifications n
                        WHERE n.user_id=@reader AND n.notification_type=e.event_key
                    );";

                int created;
                await using (var cmd = new NpgsqlCommand(sql, db, tx))
                {
                    cmd.Parameters.AddWithValue("reader", actor.UserId);
                    created = await cmd.ExecuteNonQueryAsync(ct);
                }
                await tx.CommitAsync(ct);
                return Results.Ok(new { created, message = "Đã kiểm tra nhắc hạn trả sách." });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // Do not log reader personal data or loan titles.
                app.Logger.LogError("Due-reminder sync failed: {Type}", ex.GetType().Name);
                return Results.Json(new { message = "Không đồng bộ được nhắc hạn trả. Kiểm tra API/Aiven." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });
    }

    private static string? Bearer(HttpRequest request)
    {
        string auth = request.Headers.Authorization.ToString();
        return auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? auth[7..].Trim() : null;
    }
}
