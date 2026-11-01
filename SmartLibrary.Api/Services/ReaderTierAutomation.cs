using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace SmartLibrary.Api.Services;

// STEP 76: auto tier evaluation on the server. Reader/Staff/Admin cannot submit
// a tierId. No manual tier-management screen or schema migration required.
// Policy: STANDARD -> SILVER -> GOLD. One promotion / 30-day good period;
// one downgrade for EACH distinct copy overdue by >=3 Vietnam calendar days.
public static class ReaderTierAutomation
{
    private static readonly string[] LevelCodes = { "STANDARD", "SILVER", "GOLD" };
    private const int PromotionDays = 30;
    private const int DowngradeLateDays = 3;

    // Runs in its own transaction. A failure here NEVER rolls back book returns.
    // The profile row is locked to prevent duplicate rank changes.
    public static async Task ApplyAsync(NpgsqlConnection db, NpgsqlTransaction tx,
        long readerUserId, CancellationToken ct, long justReturnedBorrowDetailId = 0)
    {
        // Do not alter ranks if the required demo tiers have not been installed.
        var tiers = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        await using (var tierCmd = new NpgsqlCommand(@"
            SELECT UPPER(code), tier_id FROM public.reader_tiers
            WHERE UPPER(code) IN ('STANDARD','SILVER','GOLD') AND is_active=TRUE;", db, tx))
        await using (var rd = await tierCmd.ExecuteReaderAsync(ct))
            while (await rd.ReadAsync(ct)) tiers[rd.GetString(0)] = rd.GetInt64(1);
        if (tiers.Count != LevelCodes.Length) return;

        string currentCode;
        DateTime profileCreated;
        await using (var profile = new NpgsqlCommand(@"
            SELECT UPPER(t.code), rp.created_at
            FROM public.reader_profiles rp
            JOIN public.reader_tiers t ON t.tier_id=rp.tier_id
            JOIN public.users u ON u.user_id=rp.user_id
            JOIN public.roles r ON r.role_id=u.role_id
            WHERE rp.user_id=@reader AND r.role_name='Reader'
              AND u.account_status='active' AND u.is_guest=FALSE
            FOR UPDATE OF rp;", db, tx))
        {
            profile.Parameters.AddWithValue("reader", readerUserId);
            await using var rd = await profile.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return;
            currentCode = rd.GetString(0);
            profileCreated = rd.GetDateTime(1);
        }

        int level = Array.FindIndex(LevelCodes,
            x => string.Equals(x, currentCode, StringComparison.OrdinalIgnoreCase));
        if (level < 0) return; // Unknown/custom tier: preserve it.
        string entity = readerUserId.ToString(CultureInfo.InvariantCulture);

        // Baseline prevents retroactively penalizing loans returned BEFORE
        // automation was installed. Currently open overdue loans are still checked.
        DateTime baseline;
        await using (var findInit = new NpgsqlCommand(@"
            SELECT MIN(created_at) FROM public.audit_logs
            WHERE entity_type='reader_tier_auto' AND entity_id=@entity
              AND action='reader_tier_auto_initialized';", db, tx))
        {
            findInit.Parameters.AddWithValue("entity", entity);
            var result = await findInit.ExecuteScalarAsync(ct);
            if (result is DateTime time) baseline = time;
            else
            {
                await using var insertInit = new NpgsqlCommand(@"
                    INSERT INTO public.audit_logs(actor_user_id,action,entity_type,entity_id,details)
                    VALUES(NULL,'reader_tier_auto_initialized','reader_tier_auto',@entity,
                           jsonb_build_object('promotionDays',30,'downgradeLateDays',3))
                    RETURNING created_at;", db, tx);
                insertInit.Parameters.AddWithValue("entity", entity);
                baseline = (DateTime)(await insertInit.ExecuteScalarAsync(ct)
                    ?? throw new InvalidOperationException("Tier auto initialization failed."));
            }
        }

        // One late COPY can trigger only one demotion. The audit check runs
        // while holding the reader profile lock, also across concurrent requests.
        var pendingLate = new List<(long DetailId, int LateDays)>();
        await using (var findLate = new NpgsqlCommand(@"
            SELECT d.borrow_detail_id,
                   CASE WHEN d.closed_at IS NULL
                      THEN GREATEST(0,(NOW() AT TIME ZONE 'Asia/Ho_Chi_Minh')::date
                           -(d.due_at AT TIME ZONE 'Asia/Ho_Chi_Minh')::date)
                      ELSE GREATEST(0,(d.returned_at AT TIME ZONE 'Asia/Ho_Chi_Minh')::date
                           -(d.due_at AT TIME ZONE 'Asia/Ho_Chi_Minh')::date) END AS late_days
            FROM public.borrow_details d
            JOIN public.borrows b ON b.borrow_id=d.borrow_id
            WHERE b.reader_user_id=@reader
              AND (d.closed_at IS NULL OR
                   (d.close_reason='returned' AND (d.returned_at>=@baseline
                      OR d.borrow_detail_id=@justReturned)))
              AND CASE WHEN d.closed_at IS NULL
                    THEN (NOW() AT TIME ZONE 'Asia/Ho_Chi_Minh')::date
                         -(d.due_at AT TIME ZONE 'Asia/Ho_Chi_Minh')::date
                    ELSE (d.returned_at AT TIME ZONE 'Asia/Ho_Chi_Minh')::date
                         -(d.due_at AT TIME ZONE 'Asia/Ho_Chi_Minh')::date END >= @days
              AND NOT EXISTS (
                SELECT 1 FROM public.audit_logs a
                WHERE a.entity_type='reader_tier_auto' AND a.entity_id=@entity
                  AND a.action='reader_tier_auto_late'
                  AND a.details->>'borrowDetailId'=d.borrow_detail_id::text)
            ORDER BY d.borrow_detail_id LIMIT 100;", db, tx))
        {
            findLate.Parameters.AddWithValue("reader", readerUserId);
            findLate.Parameters.AddWithValue("entity", entity);
            findLate.Parameters.AddWithValue("baseline", baseline);
            findLate.Parameters.AddWithValue("justReturned", justReturnedBorrowDetailId);
            findLate.Parameters.AddWithValue("days", DowngradeLateDays);
            await using var rd = await findLate.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) pendingLate.Add((rd.GetInt64(0), rd.GetInt32(1)));
        }

        foreach (var overdue in pendingLate)
        {
            string before = LevelCodes[level];
            if (level > 0)
            {
                level--;
                await UpdateTierAsync(db, tx, readerUserId, tiers[LevelCodes[level]], ct);
            }
            string after = LevelCodes[level];
            await using (var audit = new NpgsqlCommand(@"
                INSERT INTO public.audit_logs(actor_user_id,action,entity_type,entity_id,details)
                VALUES(NULL,'reader_tier_auto_late','reader_tier_auto',@entity,
                  jsonb_build_object('borrowDetailId',@detail,'lateDays',@late,
                                     'from',@from,'to',@to));", db, tx))
            {
                audit.Parameters.AddWithValue("entity", entity);
                audit.Parameters.AddWithValue("detail", overdue.DetailId);
                audit.Parameters.AddWithValue("late", overdue.LateDays);
                audit.Parameters.AddWithValue("from", before);
                audit.Parameters.AddWithValue("to", after);
                await audit.ExecuteNonQueryAsync(ct);
            }
            if (before != after)
                await NotifyAsync(db, tx, readerUserId, "Hạng độc giả tự động giảm",
                    $"Cuốn mượn trả/chưa trả trễ từ {DowngradeLateDays} ngày. Hạng đổi từ {before} xuống {after}.", ct);
        }

        // A demotion blocks promotion in this same evaluation.
        if (pendingLate.Count > 0 || level >= LevelCodes.Length - 1) return;
        var lastChange = profileCreated;
        await using (var last = new NpgsqlCommand(@"
            SELECT MAX(created_at) FROM public.audit_logs
            WHERE entity_type='reader_tier_auto' AND entity_id=@entity
              AND action IN ('reader_tier_auto_promoted','reader_tier_auto_late')
              AND COALESCE(details->>'from','')<>COALESCE(details->>'to','');", db, tx))
        {
            last.Parameters.AddWithValue("entity", entity);
            var result = await last.ExecuteScalarAsync(ct);
            if (result is DateTime time && time > lastChange) lastChange = time;
        }
        if (lastChange > DateTime.UtcNow.AddDays(-PromotionDays)) return;

        bool qualified;
        await using (var good = new NpgsqlCommand(@"
            SELECT
              EXISTS (SELECT 1 FROM public.borrow_details d
                      JOIN public.borrows b ON b.borrow_id=d.borrow_id
                      WHERE b.reader_user_id=@reader AND d.close_reason='returned'
                        AND d.returned_at >= NOW()-INTERVAL '30 days'
                        AND d.returned_at >= @lastchange
                        AND d.returned_at <= d.due_at)
              AND NOT EXISTS (SELECT 1 FROM public.borrow_details d
                      JOIN public.borrows b ON b.borrow_id=d.borrow_id
                      WHERE b.reader_user_id=@reader AND d.close_reason='returned'
                        AND d.returned_at>=NOW()-INTERVAL '30 days'
                        AND d.returned_at>d.due_at)
              AND NOT EXISTS (SELECT 1 FROM public.borrow_details d
                      JOIN public.borrows b ON b.borrow_id=d.borrow_id
                      WHERE b.reader_user_id=@reader AND d.closed_at IS NULL
                        AND d.due_at<NOW())
              AND NOT EXISTS (SELECT 1 FROM public.fines f
                      JOIN public.borrow_details d ON d.borrow_detail_id=f.borrow_detail_id
                      JOIN public.borrows b ON b.borrow_id=d.borrow_id
                      LEFT JOIN (SELECT fine_id,SUM(amount) AS paid
                                 FROM public.fine_payments GROUP BY fine_id) p ON p.fine_id=f.fine_id
                      WHERE b.reader_user_id=@reader
                        AND f.amount-f.waived_amount-COALESCE(p.paid,0)>0);", db, tx))
        {
            good.Parameters.AddWithValue("reader", readerUserId);
            good.Parameters.AddWithValue("lastchange", lastChange);
            qualified = (bool)(await good.ExecuteScalarAsync(ct) ?? false);
        }
        if (!qualified) return;

        string oldTier = LevelCodes[level];
        string newTier = LevelCodes[level + 1];
        await UpdateTierAsync(db, tx, readerUserId, tiers[newTier], ct);
        await using (var audit = new NpgsqlCommand(@"
            INSERT INTO public.audit_logs(actor_user_id,action,entity_type,entity_id,details)
            VALUES(NULL,'reader_tier_auto_promoted','reader_tier_auto',@entity,
                jsonb_build_object('from',@from,'to',@to,'goodDays',30));", db, tx))
        {
            audit.Parameters.AddWithValue("entity", entity);
            audit.Parameters.AddWithValue("from", oldTier);
            audit.Parameters.AddWithValue("to", newTier);
            await audit.ExecuteNonQueryAsync(ct);
        }
        await NotifyAsync(db, tx, readerUserId, "Chúc mừng bạn được tăng hạng độc giả",
            $"Bạn đã mượn/trả đúng hạn trong kỳ xét 30 ngày. Hạng đổi từ {oldTier} lên {newTier}.", ct);
    }

    private static async Task UpdateTierAsync(NpgsqlConnection db, NpgsqlTransaction tx,
        long reader, long tierId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(@"
            UPDATE public.reader_profiles SET tier_id=@tier WHERE user_id=@reader;", db, tx);
        cmd.Parameters.AddWithValue("tier", tierId);
        cmd.Parameters.AddWithValue("reader", reader);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task NotifyAsync(NpgsqlConnection db, NpgsqlTransaction tx,
        long reader, string title, string body, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO public.notifications(user_id,notification_type,title,body)
            VALUES(@reader,'reader_tier_changed',@title,@body);", db, tx);
        cmd.Parameters.AddWithValue("reader", reader);
        cmd.Parameters.AddWithValue("title", title);
        cmd.Parameters.AddWithValue("body", body);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

// No extra worker project, no user interface: runs once after backend starts
// and periodically while the API is running (Development only).
public sealed class ReaderTierAutoHostedService : BackgroundService
{
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<ReaderTierAutoHostedService> _logger;

    public ReaderTierAutoHostedService(IConfiguration configuration,
        IHostEnvironment environment, ILogger<ReaderTierAutoHostedService> logger)
    {
        _configuration = configuration;
        _environment = environment;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_environment.IsDevelopment()) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ProcessAllAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            { _logger.LogWarning("Automatic tier processing skipped: {ErrorType}", ex.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromHours(12), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private async Task ProcessAllAsync(CancellationToken ct)
    {
        string? cs = _configuration.GetConnectionString("AivenPostgres");
        if (string.IsNullOrWhiteSpace(cs)) return;
        var ids = new List<long>();
        await using (var db = new NpgsqlConnection(cs))
        {
            await db.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(@"
                SELECT rp.user_id FROM public.reader_profiles rp
                JOIN public.users u ON u.user_id=rp.user_id
                JOIN public.roles r ON r.role_id=u.role_id
                WHERE r.role_name='Reader' AND u.account_status='active'
                      AND u.is_guest=FALSE ORDER BY rp.user_id;", db);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) ids.Add(rd.GetInt64(0));
        }
        foreach (long id in ids)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await using var db = new NpgsqlConnection(cs);
                await db.OpenAsync(ct);
                await using var tx = await db.BeginTransactionAsync(ct);
                await ReaderTierAutomation.ApplyAsync(db, tx, id, ct);
                await tx.CommitAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // No personal data in logs; one bad profile doesn't block all others.
                _logger.LogWarning("Reader auto tier evaluation failed: {ErrorType}", ex.GetType().Name);
            }
        }
    }
}
