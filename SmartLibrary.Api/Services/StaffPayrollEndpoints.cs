using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Npgsql;
using NpgsqlTypes;

namespace SmartLibrary.Api.Services;

// Step 80: payroll based on completed attendance shifts and the rate saved at check-in.
// Routes remain Development-only, like the existing loopback API. Admin is enforced server-side.
public static class StaffPayrollEndpoints
{
    public sealed class MonthRequest { public string? Month { get; set; } }
    public sealed class RateRequest { public decimal HourlyRate { get; set; } public string? Reason { get; set; } }

    public static void MapStaffPayrollEndpoints(this WebApplication app)
    {
        app.MapGet("/api/admin/payroll", async (string? month, HttpRequest req,
            SessionService sessions, IConfiguration cfg, CancellationToken ct) =>
        {
            var denial = Deny(app, req, sessions, out _);
            if (denial != null) return denial;
            if (!TryMonth(month, out var first, out var end))
                return Results.BadRequest(new { message = "Tháng phải theo YYYY-MM (2000-2100)." });
            try
            {
                await using var db = await Connect(cfg, ct);
                var rows = new List<object>();
                await using var cmd = new NpgsqlCommand(@"
                    SELECT s.user_id, s.staff_code, s.full_name, s.hourly_rate,
                           COALESCE(p.salary_id, 0), COALESCE(p.status, 'not_prepared'),
                           COALESCE(p.total_hours, 0), COALESCE(p.total_amount, 0),
                           (SELECT COUNT(*) FROM public.timesheets t
                             WHERE t.staff_user_id=s.user_id AND t.work_date>=@start AND t.work_date<@finish
                               AND t.check_out_at IS NOT NULL),
                           (SELECT COUNT(*) FROM public.timesheets t
                             WHERE t.staff_user_id=s.user_id AND t.work_date>=@start AND t.work_date<@finish
                               AND t.check_out_at IS NULL)
                      FROM public.staff_profiles s
                      LEFT JOIN public.salaries p ON p.staff_user_id=s.user_id AND p.salary_month=@start
                     ORDER BY s.staff_code, s.user_id;", db);
                MonthParams(cmd, first, end);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    rows.Add(new
                    {
                        staffUserId = reader.GetInt64(0),
                        staffCode = reader.GetString(1),
                        fullName = reader.GetString(2),
                        currentHourlyRate = reader.GetDecimal(3),
                        salaryId = reader.GetInt64(4),
                        status = reader.GetString(5),
                        totalHours = reader.GetDecimal(6),
                        totalAmount = reader.GetDecimal(7),
                        closedShifts = reader.GetInt64(8),
                        openShifts = reader.GetInt64(9)
                    });
                }
                return Results.Ok(new { month = first.ToString("yyyy-MM", CultureInfo.InvariantCulture), rows });
            }
            catch (Exception ex) { return Fail(app, ex, "Không tải được bảng lương."); }
        });

        app.MapGet("/api/admin/payroll/{salaryId:long}/lines", async (long salaryId, HttpRequest req,
            SessionService sessions, IConfiguration cfg, CancellationToken ct) =>
        {
            var denial = Deny(app, req, sessions, out _);
            if (denial != null) return denial;
            if (salaryId <= 0) return Results.BadRequest(new { message = "Chọn bảng lương đã tính." });
            try
            {
                await using var db = await Connect(cfg, ct);
                var lines = new List<object>();
                await using var cmd = new NpgsqlCommand(@"
                    SELECT l.timesheet_id, to_char(t.check_in_at AT TIME ZONE 'Asia/Ho_Chi_Minh', 'DD/MM/YYYY HH24:MI'),
                           to_char(t.check_out_at AT TIME ZONE 'Asia/Ho_Chi_Minh', 'DD/MM/YYYY HH24:MI'),
                           l.hours_snapshot, l.hourly_rate_snapshot, l.line_amount
                      FROM public.salary_lines l
                      JOIN public.timesheets t ON t.timesheet_id=l.timesheet_id
                     WHERE l.salary_id=@id ORDER BY t.check_in_at, t.timesheet_id;", db);
                cmd.Parameters.AddWithValue("id", salaryId);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    lines.Add(new
                    {
                        timesheetId = reader.GetInt64(0),
                        checkIn = reader.GetString(1),
                        checkOut = reader.GetString(2),
                        hours = reader.GetDecimal(3),
                        hourlyRate = reader.GetDecimal(4),
                        amount = reader.GetDecimal(5)
                    });
                return Results.Ok(new { lines });
            }
            catch (Exception ex) { return Fail(app, ex, "Không tải được chi tiết bảng lương."); }
        });

        // Recalculate drafts; NEVER rewrite finalized or paid salary records.
        app.MapPost("/api/admin/payroll/prepare", async (MonthRequest input, HttpRequest req,
            SessionService sessions, IConfiguration cfg, CancellationToken ct) =>
        {
            var denial = Deny(app, req, sessions, out var actorId);
            if (denial != null) return denial;
            if (!TryMonth(input.Month, out var first, out var end))
                return Results.BadRequest(new { message = "Tháng phải theo YYYY-MM (2000-2100)." });
            try
            {
                await using var db = await Connect(cfg, ct);
                await using var tx = await db.BeginTransactionAsync(ct);
                var staffIds = new List<long>();
                // Serialize salary preparation, and rate edits that update staff_profiles.
                await using (var select = new NpgsqlCommand("SELECT user_id FROM public.staff_profiles ORDER BY user_id FOR UPDATE", db, tx))
                await using (var rd = await select.ExecuteReaderAsync(ct))
                    while (await rd.ReadAsync(ct)) staffIds.Add(rd.GetInt64(0));
                var prepared = 0; var locked = 0;
                foreach (var staffId in staffIds)
                {
                    long? salaryId;
                    await using (var upsert = new NpgsqlCommand(@"
                        INSERT INTO public.salaries(staff_user_id,salary_month,total_hours,total_amount,status)
                        VALUES(@staff,@month,0,0,'draft')
                        ON CONFLICT(staff_user_id,salary_month) DO UPDATE
                             SET total_hours=0,total_amount=0
                           WHERE salaries.status='draft'
                        RETURNING salary_id;", db, tx))
                    {
                        upsert.Parameters.AddWithValue("staff", staffId);
                        upsert.Parameters.AddWithValue("month", NpgsqlDbType.Date, first);
                        var obj = await upsert.ExecuteScalarAsync(ct);
                        salaryId = obj is null ? null : Convert.ToInt64(obj, CultureInfo.InvariantCulture);
                    }
                    if (salaryId is null) { locked++; continue; }
                    await using (var remove = new NpgsqlCommand("DELETE FROM public.salary_lines WHERE salary_id=@id", db, tx))
                    {
                        remove.Parameters.AddWithValue("id", salaryId.Value);
                        await remove.ExecuteNonQueryAsync(ct);
                    }
                    await using (var create = new NpgsqlCommand(@"
                        INSERT INTO public.salary_lines(salary_id,timesheet_id,hours_snapshot,hourly_rate_snapshot,line_amount)
                        SELECT @id,t.timesheet_id,
                               ROUND((EXTRACT(EPOCH FROM t.check_out_at-t.check_in_at)/3600)::numeric,2),
                               t.hourly_rate_snapshot,
                               ROUND(ROUND((EXTRACT(EPOCH FROM t.check_out_at-t.check_in_at)/3600)::numeric,2)
                                     * t.hourly_rate_snapshot,2)
                          FROM public.timesheets t
                         WHERE t.staff_user_id=@staff AND t.work_date>=@start AND t.work_date<@finish
                           AND t.check_out_at IS NOT NULL;", db, tx))
                    {
                        create.Parameters.AddWithValue("id", salaryId.Value);
                        create.Parameters.AddWithValue("staff", staffId);
                        MonthParams(create, first, end);
                        await create.ExecuteNonQueryAsync(ct);
                    }
                    await using (var total = new NpgsqlCommand(@"
                        UPDATE public.salaries SET total_hours=COALESCE((SELECT SUM(hours_snapshot)
                          FROM public.salary_lines WHERE salary_id=@id),0),
                          total_amount=COALESCE((SELECT SUM(line_amount)
                          FROM public.salary_lines WHERE salary_id=@id),0)
                         WHERE salary_id=@id;", db, tx))
                    {
                        total.Parameters.AddWithValue("id", salaryId.Value);
                        await total.ExecuteNonQueryAsync(ct);
                    }
                    prepared++;
                }
                await Audit(db, tx, actorId, "payroll_prepare", "salary_month", first.ToString("yyyy-MM"),
                    new { month = first.ToString("yyyy-MM"), prepared, skippedLocked = locked }, ct);
                await tx.CommitAsync(ct);
                return Results.Ok(new { message = $"Đã tính lại {prepared} bảng lương nháp; bỏ qua {locked} bảng đã chốt/đã trả.", prepared, skippedLocked = locked });
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            { return Results.Conflict(new { message = "Một ca làm đã thuộc bảng lương khác. Kiểm tra salary_lines trước khi tính lại." }); }
            catch (Exception ex) { return Fail(app, ex, "Không tính được bảng lương."); }
        });

        app.MapPost("/api/admin/payroll/{salaryId:long}/finalize", async (long salaryId, HttpRequest req,
            SessionService sessions, IConfiguration cfg, CancellationToken ct) =>
        {
            var denial = Deny(app, req, sessions, out var actorId);
            if (denial != null) return denial;
            try
            {
                await using var db = await Connect(cfg, ct);
                await using var tx = await db.BeginTransactionAsync(ct);
                // Only closed calendar months may be finalized, preventing pay omission from future shifts.
                await using var cmd = new NpgsqlCommand(@"
                    UPDATE public.salaries s SET status='finalized', finalized_at=NOW()
                     WHERE s.salary_id=@id AND s.status='draft'
                       AND s.salary_month < date_trunc('month', NOW() AT TIME ZONE 'Asia/Ho_Chi_Minh')::date
                       AND NOT EXISTS(SELECT 1 FROM public.timesheets t
                           WHERE t.staff_user_id=s.staff_user_id AND t.work_date>=s.salary_month
                             AND t.work_date<s.salary_month+INTERVAL '1 month' AND t.check_out_at IS NULL)
                       AND NOT EXISTS(SELECT 1 FROM public.timesheets t
                           WHERE t.staff_user_id=s.staff_user_id AND t.work_date>=s.salary_month
                             AND t.work_date<s.salary_month+INTERVAL '1 month' AND t.check_out_at IS NOT NULL
                             AND NOT EXISTS(SELECT 1 FROM public.salary_lines l
                                 WHERE l.salary_id=s.salary_id AND l.timesheet_id=t.timesheet_id))
                     RETURNING s.staff_user_id;", db, tx);
                cmd.Parameters.AddWithValue("id", salaryId);
                var result = await cmd.ExecuteScalarAsync(ct);
                if (result is null)
                    return Results.Conflict(new { message = "Chỉ chốt tháng đã kết thúc, trạng thái nháp, không có ca mở và đủ các ca. Hãy tính lại bảng lương trước." });
                await Audit(db, tx, actorId, "payroll_finalize", "salary", salaryId.ToString(),
                    new { salaryId }, ct);
                await tx.CommitAsync(ct);
                return Results.Ok(new { message = "Đã CHỐT bảng lương. Số giờ và số tiền được khóa." });
            }
            catch (Exception ex) { return Fail(app, ex, "Không chốt được bảng lương."); }
        });

        app.MapPost("/api/admin/payroll/{salaryId:long}/paid", async (long salaryId, HttpRequest req,
            SessionService sessions, IConfiguration cfg, CancellationToken ct) =>
        {
            var denial = Deny(app, req, sessions, out var actorId);
            if (denial != null) return denial;
            try
            {
                await using var db = await Connect(cfg, ct);
                await using var tx = await db.BeginTransactionAsync(ct);
                await using var cmd = new NpgsqlCommand(@"
                    UPDATE public.salaries SET status='paid', paid_at=NOW()
                     WHERE salary_id=@id AND status='finalized' RETURNING staff_user_id;", db, tx);
                cmd.Parameters.AddWithValue("id", salaryId);
                var result = await cmd.ExecuteScalarAsync(ct);
                if (result is null)
                    return Results.Conflict(new { message = "Chỉ được đánh dấu đã trả cho bảng lương đã chốt." });
                await Audit(db, tx, actorId, "payroll_mark_paid", "salary", salaryId.ToString(),
                    new { salaryId, note = "Xác nhận thủ công, không chuyển tiền tự động" }, ct);
                await tx.CommitAsync(ct);
                return Results.Ok(new { message = "Đã ghi nhận trạng thái ĐÃ TRẢ (thao tác thủ công)." });
            }
            catch (Exception ex) { return Fail(app, ex, "Không cập nhật được trạng thái trả lương."); }
        });

        app.MapPost("/api/admin/payroll/staff/{staffId:long}/rate", async (long staffId,
            RateRequest input, HttpRequest req, SessionService sessions,
            IConfiguration cfg, CancellationToken ct) =>
        {
            var denial = Deny(app, req, sessions, out var actorId);
            if (denial != null) return denial;
            var reason = (input.Reason ?? "").Trim();
            if (staffId <= 0 || input.HourlyRate <= 0 || input.HourlyRate > 10000000m ||
                reason.Length < 8 || reason.Length > 300)
                return Results.BadRequest(new { message = "Đơn giá phải > 0 và <= 10 triệu VND/giờ; lý do từ 8–300 ký tự." });
            try
            {
                await using var db = await Connect(cfg, ct);
                await using var tx = await db.BeginTransactionAsync(ct);
                decimal oldRate;
                await using (var get = new NpgsqlCommand("SELECT hourly_rate FROM public.staff_profiles WHERE user_id=@id FOR UPDATE", db, tx))
                {
                    get.Parameters.AddWithValue("id", staffId);
                    var value = await get.ExecuteScalarAsync(ct);
                    if (value is null) return Results.NotFound(new { message = "Không tìm thấy Staff." });
                    oldRate = (decimal)value;
                }
                await using (var update = new NpgsqlCommand("UPDATE public.staff_profiles SET hourly_rate=@rate WHERE user_id=@id", db, tx))
                {
                    update.Parameters.AddWithValue("id", staffId);
                    update.Parameters.AddWithValue("rate", input.HourlyRate);
                    await update.ExecuteNonQueryAsync(ct);
                }
                await Audit(db, tx, actorId, "staff_hourly_rate_change", "staff_profile", staffId.ToString(),
                    new { staffId, oldRate, newRate = input.HourlyRate, reason }, ct);
                await tx.CommitAsync(ct);
                return Results.Ok(new { message = "Đã cập nhật đơn giá cho các ca CHECK-IN TỪ BÂY GIỜ. Ca cũ không bị đổi." });
            }
            catch (Exception ex) { return Fail(app, ex, "Không cập nhật được đơn giá giờ công."); }
        });
    }

    private static bool TryMonth(string? month, out DateOnly first, out DateOnly end)
    {
        first = default; end = default;
        if (string.IsNullOrWhiteSpace(month) ||
            !DateOnly.TryParseExact(month.Trim() + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out first) || first.Year < 2000 || first.Year > 2100)
            return false;
        end = first.AddMonths(1);
        return true;
    }
    private static void MonthParams(NpgsqlCommand cmd, DateOnly first, DateOnly end)
    {
        cmd.Parameters.AddWithValue("start", NpgsqlDbType.Date, first);
        cmd.Parameters.AddWithValue("finish", NpgsqlDbType.Date, end);
    }
    private static IResult? Deny(WebApplication app, HttpRequest req, SessionService session,
        out long actorId)
    {
        actorId = 0;
        if (!app.Environment.IsDevelopment()) return Results.NotFound();
        var actor = session.Validate(Bearer(req));
        if (actor is null) return Results.Unauthorized();
        if (actor.Role != "Admin") return Results.StatusCode(403);
        actorId = actor.UserId;
        return null;
    }
    private static string? Bearer(HttpRequest req)
    {
        var header = req.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        var token = header.Substring(7).Trim();
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
    private static async Task Audit(NpgsqlConnection db, NpgsqlTransaction tx, long actor,
        string action, string entityType, string entityId, object details, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO public.audit_logs(actor_user_id,action,entity_type,entity_id,details)
            VALUES(@actor,@action,@type,@id,@details::jsonb)", db, tx);
        cmd.Parameters.AddWithValue("actor", actor);
        cmd.Parameters.AddWithValue("action", action);
        cmd.Parameters.AddWithValue("type", entityType);
        cmd.Parameters.AddWithValue("id", entityId);
        cmd.Parameters.AddWithValue("details", JsonSerializer.Serialize(details));
        await cmd.ExecuteNonQueryAsync(ct);
    }
    private static IResult Fail(WebApplication app, Exception ex, string message)
    {
        app.Logger.LogError("Payroll operation failed: {Type}", ex.GetType().Name);
        return Results.Json(new { message }, statusCode: 503);
    }
}
