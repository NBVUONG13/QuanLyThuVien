using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Npgsql;

namespace SmartLibrary.Api.Services;

// Step 75: independent, Admin-only, Development-only policy endpoints.
// No direct SQL from WPF. No change to existing loan, fine, or renewal routes.
public static class LibraryPolicyEndpoints
{
    public sealed class PolicyEditRequest
    {
        public long ExpectedPolicyVersionId { get; set; }
        public int MaxRenewals { get; set; }
        public int RenewalDays { get; set; }
        public bool AllowOverdueRenewalRequests { get; set; }
        public bool AllowOverdueRenewalApproval { get; set; }
        public decimal OverdueFeePerDay { get; set; }
        public int OverdueGraceDays { get; set; }
        public decimal MaxOverdueFeePerDetail { get; set; }
        public string? Notes { get; set; }
    }

    public static void MapLibraryPolicyEndpoints(this WebApplication app)
    {
        app.MapGet("/api/admin/library-policy", async (
            HttpRequest req, SessionService sessions,
            IConfiguration config, CancellationToken ct) =>
        {
            if (!app.Environment.IsDevelopment()) return Results.NotFound();
            var actor = sessions.Validate(GetBearerToken(req));
            if (actor is null) return Results.Unauthorized();
            if (actor.Role != "Admin") return Results.StatusCode(403);

            try
            {
                string? cs = config.GetConnectionString("AivenPostgres");
                if (string.IsNullOrWhiteSpace(cs))
                    return Results.Json(new { message = "Backend chưa cấu hình kết nối database." }, statusCode: 503);

                await using var db = new NpgsqlConnection(cs);
                await db.OpenAsync(ct);
                var history = new List<object>();
                await using (var query = new NpgsqlCommand(@"
                    SELECT policy_version_id, effective_at, max_renewals, renewal_days,
                           allow_overdue_renewal_requests, allow_overdue_renewal_approval,
                           overdue_fee_per_day, overdue_grace_days, max_overdue_fee_per_detail,
                           notes, created_at
                      FROM public.borrowing_policy_versions
                     WHERE effective_at <= NOW()
                     ORDER BY effective_at DESC LIMIT 20;", db))
                {
                    await using var reader = await query.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct))
                    {
                        history.Add(new
                        {
                            policyVersionId = reader.GetInt64(0),
                            effectiveAt = reader.GetDateTime(1).ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture),
                            maxRenewals = reader.GetInt32(2),
                            renewalDays = reader.GetInt32(3),
                            allowOverdueRenewalRequests = reader.GetBoolean(4),
                            allowOverdueRenewalApproval = reader.GetBoolean(5),
                            overdueFeePerDay = reader.GetDecimal(6),
                            overdueGraceDays = reader.GetInt32(7),
                            maxOverdueFeePerDetail = reader.GetDecimal(8),
                            notes = reader.IsDBNull(9) ? "" : reader.GetString(9),
                            createdAt = reader.GetDateTime(10).ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)
                        });
                    }
                }
                return Results.Ok(new { current = history.FirstOrDefault(), history });
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.UndefinedColumn)
            { return Results.Conflict(new { message = "Thiếu bảng/cột borrowing_policy_versions. Kiểm tra schema DB-11." }); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            { return Results.StatusCode(499); }
            catch (Exception ex)
            {
                app.Logger.LogError("Read library policy failed: {Type}", ex.GetType().Name);
                return Results.Json(new { message = "Không tải được chính sách thư viện." }, statusCode: 503);
            }
        });

        app.MapPost("/api/admin/library-policy", async (
            PolicyEditRequest input, HttpRequest req, SessionService sessions,
            IConfiguration config, CancellationToken ct) =>
        {
            if (!app.Environment.IsDevelopment()) return Results.NotFound();
            var actor = sessions.Validate(GetBearerToken(req));
            if (actor is null) return Results.Unauthorized();
            if (actor.Role != "Admin") return Results.StatusCode(403);

            if (input.ExpectedPolicyVersionId < 0 ||
                input.MaxRenewals < 0 || input.MaxRenewals > 10 ||
                input.RenewalDays < 1 || input.RenewalDays > 365 ||
                input.OverdueGraceDays < 0 || input.OverdueGraceDays > 365 ||
                input.OverdueFeePerDay < 0 || input.OverdueFeePerDay > 1000000m ||
                input.MaxOverdueFeePerDetail < 0 || input.MaxOverdueFeePerDetail > 100000000m ||
                (input.Notes?.Length ?? 0) > 500)
                return Results.BadRequest(new { message = "Thông số ngoài phạm vi cho phép. Kiểm tra lại các giá trị trên form." });

            try
            {
                string? cs = config.GetConnectionString("AivenPostgres");
                if (string.IsNullOrWhiteSpace(cs))
                    return Results.Json(new { message = "Backend chưa cấu hình kết nối database." }, statusCode: 503);

                await using var db = new NpgsqlConnection(cs);
                await db.OpenAsync(ct);
                await using var tx = await db.BeginTransactionAsync(ct);
                // Serialize edits from this endpoint; stale forms may not silently overwrite policy.
                await using (var lck = new NpgsqlCommand("SELECT pg_advisory_xact_lock(2026110101);", db, tx))
                    await lck.ExecuteNonQueryAsync(ct);

                long currentId = 0;
                await using (var query = new NpgsqlCommand(@"
                    SELECT policy_version_id FROM public.borrowing_policy_versions
                     WHERE effective_at <= NOW() ORDER BY effective_at DESC LIMIT 1;", db, tx))
                {
                    object? value = await query.ExecuteScalarAsync(ct);
                    if (value is long id) currentId = id;
                }
                if (currentId != input.ExpectedPolicyVersionId)
                    return Results.Conflict(new { message = "Chính sách đã được Admin khác thay đổi. Bấm LÀM MỚI rồi kiểm tra lại trước khi lưu." });

                long newId;
                await using (var insert = new NpgsqlCommand(@"
                    INSERT INTO public.borrowing_policy_versions
                    (effective_at, max_renewals, renewal_days,
                     allow_overdue_renewal_requests, allow_overdue_renewal_approval,
                     overdue_fee_per_day, overdue_grace_days, max_overdue_fee_per_detail,
                     created_by_user_id, notes)
                    VALUES (clock_timestamp(), @max, @days, @allowReq, @allowApprove,
                            @fee, @grace, @cap, @actor, @notes)
                    RETURNING policy_version_id;", db, tx))
                {
                    insert.Parameters.AddWithValue("max", input.MaxRenewals);
                    insert.Parameters.AddWithValue("days", input.RenewalDays);
                    insert.Parameters.AddWithValue("allowReq", input.AllowOverdueRenewalRequests);
                    insert.Parameters.AddWithValue("allowApprove", input.AllowOverdueRenewalApproval);
                    insert.Parameters.AddWithValue("fee", input.OverdueFeePerDay);
                    insert.Parameters.AddWithValue("grace", input.OverdueGraceDays);
                    insert.Parameters.AddWithValue("cap", input.MaxOverdueFeePerDetail);
                    insert.Parameters.AddWithValue("actor", actor.UserId);
                    insert.Parameters.AddWithValue("notes", (object?)input.Notes?.Trim() ?? DBNull.Value);
                    newId = Convert.ToInt64(await insert.ExecuteScalarAsync(ct));
                }

                string details = JsonSerializer.Serialize(new
                {
                    previousPolicyVersionId = currentId,
                    newPolicyVersionId = newId,
                    input.MaxRenewals,
                    input.RenewalDays,
                    input.AllowOverdueRenewalRequests,
                    input.AllowOverdueRenewalApproval,
                    input.OverdueFeePerDay,
                    input.OverdueGraceDays,
                    input.MaxOverdueFeePerDetail
                });
                await using (var log = new NpgsqlCommand(@"
                    INSERT INTO public.audit_logs(actor_user_id, action, entity_type, entity_id, details)
                    VALUES(@actor, 'library_policy_created', 'borrowing_policy_versions', @id, CAST(@details AS jsonb));", db, tx))
                {
                    log.Parameters.AddWithValue("actor", actor.UserId);
                    log.Parameters.AddWithValue("id", newId.ToString(CultureInfo.InvariantCulture));
                    log.Parameters.AddWithValue("details", details);
                    await log.ExecuteNonQueryAsync(ct);
                }
                await tx.CommitAsync(ct);
                return Results.Ok(new { message = "Đã lưu phiên bản chính sách mới. Gia hạn và tiền phạt phát sinh sau đó áp dụng chính sách có hiệu lực.", policyVersionId = newId });
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.UndefinedColumn)
            { return Results.Conflict(new { message = "Thiếu bảng/cột cấu hình hoặc audit_logs. Kiểm tra schema DB-11." }); }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            { return Results.Conflict(new { message = "Phiên bản chính sách vừa được cập nhật. Làm mới và thử lại." }); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            { return Results.StatusCode(499); }
            catch (Exception ex)
            {
                app.Logger.LogError("Write library policy failed: {Type}", ex.GetType().Name);
                return Results.Json(new { message = "Không lưu được chính sách thư viện." }, statusCode: 503);
            }
        });
    }

    private static string? GetBearerToken(HttpRequest request)
    {
        string raw = request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        string token = raw.Substring(prefix.Length).Trim();
        return token.Length is > 0 and <= 200 ? token : null;
    }
}
