using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Npgsql;

namespace SmartLibrary.Api.Services;

// Step 79: real attendance. Development/loopback only, consistent with existing API.
// Staff operate on their OWN identity derived from the server session, never a user id in POST.
public static class StaffAttendanceEndpoints
{
    public static void MapStaffAttendanceEndpoints(this WebApplication app)
    {
        app.MapGet("/api/attendance/shifts", async (string? month, HttpRequest req,
            SessionService sessions, IConfiguration config, CancellationToken ct) =>
        {
            if (!ProductionAccess.IsEnabled(app)) return Results.NotFound();
            var actor = sessions.Validate(Bearer(req));
            if (actor is null) return Results.Unauthorized();
            if (actor.Role is not ("Admin" or "Staff")) return Results.StatusCode(403);

            // Vietnam does not currently observe DST. Use the business calendar for month filters.
            string selectedMonth = string.IsNullOrWhiteSpace(month)
                ? DateTime.UtcNow.AddHours(7).ToString("yyyy-MM", CultureInfo.InvariantCulture)
                : month.Trim();
            if (!DateTime.TryParseExact(selectedMonth + "-01", "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var first))
                return Results.BadRequest(new { message = "Tháng phải theo YYYY-MM." });
            if (first.Year < 2000 || first.Year > 2100)
                return Results.BadRequest(new { message = "Tháng phải nằm trong khoảng 2000-2100." });
            first = new DateTime(first.Year, first.Month, 1);
            DateTime after = first.AddMonths(1);

            try
            {
                await using var db = await Connect(config, ct);
                await using var cmd = new NpgsqlCommand(@"
                    SELECT t.timesheet_id, t.staff_user_id, s.staff_code, s.full_name,
                           to_char(t.check_in_at AT TIME ZONE 'Asia/Ho_Chi_Minh','DD/MM/YYYY HH24:MI'),
                           CASE WHEN t.check_out_at IS NULL THEN '' ELSE
                             to_char(t.check_out_at AT TIME ZONE 'Asia/Ho_Chi_Minh','DD/MM/YYYY HH24:MI') END,
                           t.check_out_at IS NULL,
                           CASE WHEN t.check_out_at IS NULL THEN 0::numeric
                                ELSE ROUND((EXTRACT(EPOCH FROM t.check_out_at - t.check_in_at)/3600)::numeric,2) END,
                           t.hourly_rate_snapshot
                      FROM public.timesheets t
                      JOIN public.staff_profiles s ON s.user_id=t.staff_user_id
                     WHERE (t.check_in_at AT TIME ZONE 'Asia/Ho_Chi_Minh')::date >= @start
                       AND (t.check_in_at AT TIME ZONE 'Asia/Ho_Chi_Minh')::date < @end
                       AND (@admin OR t.staff_user_id=@actor)
                     ORDER BY t.check_in_at DESC, t.timesheet_id DESC LIMIT 500;", db);
                cmd.Parameters.AddWithValue("start", NpgsqlTypes.NpgsqlDbType.Date, DateOnly.FromDateTime(first));
                cmd.Parameters.AddWithValue("end", NpgsqlTypes.NpgsqlDbType.Date, DateOnly.FromDateTime(after));
                cmd.Parameters.AddWithValue("admin", actor.Role == "Admin");
                cmd.Parameters.AddWithValue("actor", actor.UserId);
                var shifts = new List<object>();
                decimal totalHours = 0;
                int openCount = 0;
                await using var rd = await cmd.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct))
                {
                    var hours = rd.GetDecimal(7);
                    bool isOpen = rd.GetBoolean(6);
                    if (isOpen) openCount++;
                    else totalHours += hours;
                    shifts.Add(new
                    {
                        timesheetId = rd.GetInt64(0),
                        staffUserId = rd.GetInt64(1),
                        staffCode = rd.GetString(2),
                        fullName = rd.GetString(3),
                        checkIn = rd.GetString(4),
                        checkOut = rd.GetString(5),
                        isOpen,
                        hours,
                        hourlyRate = rd.GetDecimal(8)
                    });
                }
                return Results.Ok(new
                {
                    month = selectedMonth,
                    shifts,
                    totalHours,
                    openCount,
                    note = "Tổng giờ chỉ tính ca đã checkout, danh sách tối đa 500 ca."
                });
            }
            catch (Exception ex) { return Fail(app, ex, "Không tải được lịch sử chấm công."); }
        });

        app.MapPost("/api/attendance/check-in", async (HttpRequest req, SessionService sessions,
            IConfiguration config, CancellationToken ct) =>
        {
            if (!ProductionAccess.IsEnabled(app)) return Results.NotFound();
            var actor = sessions.Validate(Bearer(req));
            if (actor is null) return Results.Unauthorized();
            if (actor.Role != "Staff") return Results.StatusCode(403);
            try
            {
                await using var db = await Connect(config, ct);
                await using var tx = await db.BeginTransactionAsync(ct);
                long id;
                await using (var cmd = new NpgsqlCommand(@"
                    WITH moment AS (SELECT clock_timestamp() AS ts)
                    INSERT INTO public.timesheets(staff_user_id, check_in_at, work_date, hourly_rate_snapshot)
                    SELECT s.user_id, moment.ts, (moment.ts AT TIME ZONE 'Asia/Ho_Chi_Minh')::date,
                           s.hourly_rate
                      FROM public.staff_profiles s JOIN public.users u ON u.user_id=s.user_id
                      CROSS JOIN moment
                     WHERE s.user_id=@actor AND u.account_status='active'
                    RETURNING timesheet_id;", db, tx))
                {
                    cmd.Parameters.AddWithValue("actor", actor.UserId);
                    var result = await cmd.ExecuteScalarAsync(ct);
                    if (result is null) return Results.StatusCode(403);
                    id = Convert.ToInt64(result, CultureInfo.InvariantCulture);
                }
                await Audit(db, tx, actor.UserId, "attendance_check_in", id, ct);
                await tx.CommitAsync(ct);
                return Results.Ok(new { message = "Check-in thành công.", timesheetId = id });
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            { return Results.Conflict(new { message = "Bạn còn một ca đang mở. Hãy check-out trước." }); }
            catch (Exception ex) { return Fail(app, ex, "Không thực hiện được check-in."); }
        });

        app.MapPost("/api/attendance/check-out", async (HttpRequest req, SessionService sessions,
            IConfiguration config, CancellationToken ct) =>
        {
            if (!ProductionAccess.IsEnabled(app)) return Results.NotFound();
            var actor = sessions.Validate(Bearer(req));
            if (actor is null) return Results.Unauthorized();
            if (actor.Role != "Staff") return Results.StatusCode(403);
            try
            {
                await using var db = await Connect(config, ct);
                await using var tx = await db.BeginTransactionAsync(ct);
                long id;
                await using (var cmd = new NpgsqlCommand(@"
                    UPDATE public.timesheets
                       SET check_out_at=GREATEST(clock_timestamp(),check_in_at + interval '1 microsecond')
                     WHERE staff_user_id=@actor AND check_out_at IS NULL
                    RETURNING timesheet_id;", db, tx))
                {
                    cmd.Parameters.AddWithValue("actor", actor.UserId);
                    var result = await cmd.ExecuteScalarAsync(ct);
                    if (result is null) return Results.Conflict(new { message = "Không có ca đang mở để check-out." });
                    id = Convert.ToInt64(result, CultureInfo.InvariantCulture);
                }
                await Audit(db, tx, actor.UserId, "attendance_check_out", id, ct);
                await tx.CommitAsync(ct);
                return Results.Ok(new { message = "Check-out thành công.", timesheetId = id });
            }
            catch (Exception ex) { return Fail(app, ex, "Không thực hiện được check-out."); }
        });
    }

    private static async Task Audit(NpgsqlConnection db, NpgsqlTransaction tx, long actor,
        string action, long id, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO public.audit_logs(actor_user_id,action,entity_type,entity_id,details)
            VALUES(@actor,@action,'timesheet',@id,'{}'::jsonb);", db, tx);
        cmd.Parameters.AddWithValue("actor", actor);
        cmd.Parameters.AddWithValue("action", action);
        cmd.Parameters.AddWithValue("id", id.ToString(CultureInfo.InvariantCulture));
        await cmd.ExecuteNonQueryAsync(ct);
    }
    private static string? Bearer(HttpRequest req)
    {
        var value = req.Headers.Authorization.ToString();
        if (!value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        var token = value.Substring(7).Trim();
        return token.Length is > 0 and <= 200 ? token : null;
    }
    private static async Task<NpgsqlConnection> Connect(IConfiguration config, CancellationToken ct)
    {
        var cs = config.GetConnectionString("AivenPostgres") ??
            throw new InvalidOperationException("Missing AivenPostgres connection string.");
        var db = new NpgsqlConnection(cs);
        try { await db.OpenAsync(ct); return db; }
        catch { await db.DisposeAsync(); throw; }
    }
    private static IResult Fail(WebApplication app, Exception ex, string message)
    {
        app.Logger.LogError("Attendance failed: {Type}", ex.GetType().Name);
        return Results.Json(new { message }, statusCode: 503);
    }
}
