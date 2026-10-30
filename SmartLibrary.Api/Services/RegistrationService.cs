using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace SmartLibrary.Api.Services;

// CHI DE TEST LOCAL. OTP duoc in vao console cua API, KHONG gui SMS.
// KHONG dua luong nay len production: phai thay bang nha cung cap SMS/eKYC that.
public sealed class RegistrationService
{
    private static readonly TimeSpan OtpLifetime = TimeSpan.FromMinutes(5);
    private readonly object _sync = new();
    private readonly Dictionary<Guid, PendingRegistration> _pending = new();
    private readonly Dictionary<long, GuestClaim> _guestClaims = new();
    // OTP-only lookup is throttled globally in this single-process Development API.
    private DateTimeOffset _claimFailureWindowStartsAt = DateTimeOffset.MinValue;
    private int _claimFailuresInWindow;
    private readonly IConfiguration _config;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<RegistrationService> _logger;
    private readonly byte[] _otpSecret = RandomNumberGenerator.GetBytes(32);

    public RegistrationService(
        IConfiguration config,
        IHostEnvironment environment,
        ILogger<RegistrationService> logger)
    {
        _config = config;
        _environment = environment;
        _logger = logger;
    }

    public async Task<StartRegistrationResult> StartAsync(
        string? fullName,
        string? username,
        string? phone,
        string? password,
        string? guestClaimCode,
        CancellationToken cancellationToken = default)
    {
        EnsureDevelopment();

        fullName = fullName?.Trim();
        username = username?.Trim();
        string normalizedPhone = NormalizeVietnamesePhone(phone);

        if (string.IsNullOrWhiteSpace(fullName) || fullName.Length < 2 || fullName.Length > 200)
            throw new RegistrationException("invalid_name", "Ho ten phai co tu 2 den 200 ky tu.");

        if (string.IsNullOrWhiteSpace(username) ||
            !Regex.IsMatch(username, @"\A[a-zA-Z0-9._]{3,40}\z"))
            throw new RegistrationException("invalid_username", "Tai khoan: 3-40 ky tu a-z, 0-9, dau cham hoac gach duoi.");

        // guest00001... is reserved for internal walk-in identities.
        if (Regex.IsMatch(username, @"\Aguest[0-9]+\z", RegexOptions.IgnoreCase))
            throw new RegistrationException("reserved_username", "Ten dang nhap guest00001... duoc danh rieng cho ho so tai quay.");

        if (string.IsNullOrWhiteSpace(password) || password.Length < 10 || password.Length > 128 ||
            !password.Any(char.IsLetter) || !password.Any(char.IsDigit))
            throw new RegistrationException("invalid_password", "Mat khau 10-128 ky tu, can co chu va so.");

        guestClaimCode = (guestClaimCode ?? "").Trim();
        bool upgrading = guestClaimCode.Length > 0;
        if (upgrading && !Regex.IsMatch(guestClaimCode, @"\A[0-9]{8}\z"))
            throw new RegistrationException("invalid_guest_claim", "Nhap ma lien ket 8 so do Staff cap.");

        // Resolve the Guest server-side: the visitor never needs to know a guest00001 code.
        // Resolving here does NOT consume the claim; reservation is atomic below.
        long guestUserId = 0;
        if (upgrading)
        {
            lock (_sync) guestUserId = ResolveGuestClaim(guestClaimCode, DateTimeOffset.UtcNow);
        }
        await using (var connection = new NpgsqlConnection(GetConnectionString()))
        {
            await connection.OpenAsync(cancellationToken);
            if (upgrading)
            {
                await using var guest = new NpgsqlCommand(@"
                    SELECT u.user_id, u.is_guest, u.account_status,
                           rp.full_name, COALESCE(rp.phone_e164,'')
                    FROM public.users u
                    JOIN public.reader_profiles rp ON rp.user_id=u.user_id
                    JOIN public.roles r ON r.role_id=u.role_id
                    WHERE u.user_id=@guest_id AND r.role_name='Reader';", connection);
                guest.Parameters.AddWithValue("guest_id", guestUserId);
                await using var rd = await guest.ExecuteReaderAsync(cancellationToken);
                if (!await rd.ReadAsync(cancellationToken) || !rd.GetBoolean(1)
                    || rd.GetString(2) != "active"
                    || (rd.GetString(4).Length > 0 && rd.GetString(4) != normalizedPhone))
                    throw new RegistrationException("guest_mismatch", "Ho so Guest khong khop. Hay yeu cau Staff kiem tra tai quay.");
                guestUserId = rd.GetInt64(0);
            }
            await using var check = new NpgsqlCommand(@"
                SELECT EXISTS (SELECT 1 FROM public.users WHERE LOWER(username)=LOWER(@username)),
                       EXISTS (SELECT 1 FROM public.reader_profiles
                               WHERE phone_e164=@phone AND user_id<>@except_id);", connection);
            check.Parameters.AddWithValue("username", username);
            check.Parameters.AddWithValue("phone", normalizedPhone);
            check.Parameters.AddWithValue("except_id", guestUserId);
            await using var found = await check.ExecuteReaderAsync(cancellationToken);
            await found.ReadAsync(cancellationToken);
            if (found.GetBoolean(0) || found.GetBoolean(1))
                throw new RegistrationException("account_exists", "Tai khoan/SĐT da co ho so. Neu la Guest, hay xin ma lien ket tai quay.");
        }

        // Khong giu mat khau ro trong bo nho. BCrypt phai trung EnhancedVerify o AuthService.
        string passwordHash = BCrypt.Net.BCrypt.EnhancedHashPassword(password, workFactor: 12);
        Guid requestId = Guid.NewGuid();
        string otp = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var item = new PendingRegistration(
            fullName, username, normalizedPhone, passwordHash,
            ComputeOtpHash(requestId, otp), now.Add(OtpLifetime), guestUserId);

        lock (_sync)
        {
            RemoveExpired(now);
            if (_pending.Count >= 200 ||
                _pending.Values.Any(x =>
                    string.Equals(x.Phone, normalizedPhone, StringComparison.Ordinal) ||
                    string.Equals(x.Username, username, StringComparison.OrdinalIgnoreCase)))
                throw new RegistrationException("try_later", "Dang co yeu cau cho xu ly. Thu lai sau 5 phut.");
            if (guestUserId > 0)
                ReserveGuestClaim(guestUserId, guestClaimCode, requestId, now);
            _pending.Add(requestId, item);
        }

        // Chỉ môi trường Development; tuyệt đối không ghi OTP vào log production.
        _logger.LogWarning("[DEV OTP - KHONG PHAI SMS] RequestId={RequestId} | OTP={Otp}", requestId, otp);
        return new StartRegistrationResult(requestId, item.ExpiresAt);
    }

    public async Task<CompleteRegistrationResult> ConfirmAsync(
        Guid requestId,
        string? otp,
        CancellationToken cancellationToken = default)
    {
        EnsureDevelopment();
        PendingRegistration item;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (_sync)
        {
            if (!_pending.TryGetValue(requestId, out var found))
                throw new RegistrationException("invalid_otp", "Ma xac nhan khong hop le hoac da het han.");
            item = found;
            if (item.ExpiresAt <= now)
            {
                _pending.Remove(requestId);
                throw new RegistrationException("invalid_otp", "Ma xac nhan khong hop le hoac da het han.");
            }
            if (item.Attempts >= 5 ||
                string.IsNullOrWhiteSpace(otp) ||
                !Regex.IsMatch(otp, @"\A\d{6}\z") ||
                !CryptographicOperations.FixedTimeEquals(
                    item.OtpHash, ComputeOtpHash(requestId, otp)))
            {
                item.Attempts++;
                if (item.Attempts >= 5) _pending.Remove(requestId);
                throw new RegistrationException("invalid_otp", "Ma xac nhan sai, het han hoac da vuot so lan thu.");
            }
            // The staff claim must remain valid and reserved for exactly this registration.
            if (item.GuestUserId > 0)
            {
                if (!_guestClaims.TryGetValue(item.GuestUserId, out var claim)
                    || claim.PendingRequestId != requestId || claim.ExpiresAt <= now)
                {
                    _pending.Remove(requestId);
                    throw new RegistrationException("guest_claim_expired", "Ma lien ket Guest da het han. Hay den quay xin cap lai.");
                }
            }
            // Lay khoi bo nho ngay khi OTP dung de ngan xac nhan lap lai.
            _pending.Remove(requestId);
        }

        await using var connection = new NpgsqlConnection(GetConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            long userId;
            if (item.GuestUserId > 0)
            {
                // Reuse the same user_id/profile; never clone a Guest or move history.
                long verifyingStaffId;
                lock (_sync)
                {
                    if (!_guestClaims.TryGetValue(item.GuestUserId, out var claim)
                        || claim.PendingRequestId != requestId || claim.ExpiresAt <= DateTimeOffset.UtcNow)
                        throw new RegistrationException("guest_claim_expired", "Ma lien ket da het han.");
                    verifyingStaffId = claim.StaffUserId;
                }
                string previousFullName;
                await using (var locked = new NpgsqlCommand(@"
                    SELECT u.is_guest, u.account_status, rp.full_name,
                           COALESCE(rp.phone_e164,'')
                    FROM public.users u JOIN public.reader_profiles rp ON rp.user_id=u.user_id
                    WHERE u.user_id=@id FOR UPDATE OF u, rp;", connection, transaction))
                {
                    locked.Parameters.AddWithValue("id", item.GuestUserId);
                    await using var r = await locked.ExecuteReaderAsync(cancellationToken);
                    if (!await r.ReadAsync(cancellationToken) || !r.GetBoolean(0)
                        || r.GetString(1) != "active"
                        || (r.GetString(3).Length > 0 && r.GetString(3) != item.Phone))
                        throw new RegistrationException("guest_mismatch", "Guest da doi trang thai hoac thong tin lien he khong khop.");
                    previousFullName = r.GetString(2).Trim();
                }
                await using (var update = new NpgsqlCommand(@"
                    UPDATE public.users SET username=@username, password_hash=@hash, is_guest=FALSE
                    WHERE user_id=@id AND is_guest=TRUE RETURNING user_id;", connection, transaction))
                {
                    update.Parameters.AddWithValue("username", item.Username);
                    update.Parameters.AddWithValue("hash", item.PasswordHash);
                    update.Parameters.AddWithValue("id", item.GuestUserId);
                    if (await update.ExecuteScalarAsync(cancellationToken) is null)
                        throw new RegistrationException("guest_mismatch", "Guest da duoc lien ket truoc do.");
                }
                await using (var profile = new NpgsqlCommand(@"
                    UPDATE public.reader_profiles
                    SET full_name=@full_name,
                        phone_e164=COALESCE(phone_e164,@phone),
                        phone_verified_at=COALESCE(phone_verified_at,CURRENT_TIMESTAMP)
                    WHERE user_id=@id;", connection, transaction))
                {
                    profile.Parameters.AddWithValue("full_name", item.FullName);
                    profile.Parameters.AddWithValue("phone", item.Phone);
                    profile.Parameters.AddWithValue("id", item.GuestUserId);
                    await profile.ExecuteNonQueryAsync(cancellationToken);
                }
                await using (var audit = new NpgsqlCommand(@"
                    INSERT INTO public.audit_logs(actor_user_id,action,entity_type,entity_id,details)
                    VALUES(@staff,'guest_reader_activated','guest_activation',@entity,
                           jsonb_build_object('mode','staff_claim_and_dev_otp',
                               'name_changed',@name_changed));", connection, transaction))
                {
                    audit.Parameters.AddWithValue("staff", verifyingStaffId);
                    audit.Parameters.AddWithValue("name_changed",
                        !string.Equals(previousFullName, item.FullName, StringComparison.Ordinal));
                    audit.Parameters.AddWithValue("entity", item.GuestUserId.ToString(CultureInfo.InvariantCulture));
                    await audit.ExecuteNonQueryAsync(cancellationToken);
                }
                await transaction.CommitAsync(cancellationToken);
                lock (_sync) _guestClaims.Remove(item.GuestUserId);
                return new CompleteRegistrationResult(item.GuestUserId, item.Username, "Reader");
            }
            long roleId;
            long tierId;

            await using (var roleCmd = new NpgsqlCommand(
                "SELECT role_id FROM public.roles WHERE role_name = 'Reader' LIMIT 1;", connection, transaction))
            {
                object? value = await roleCmd.ExecuteScalarAsync(cancellationToken);
                if (value is null)
                    throw new InvalidOperationException("Chua co vai tro Reader trong public.roles.");
                roleId = Convert.ToInt64(value, CultureInfo.InvariantCulture);
            }

            await using (var tierCmd = new NpgsqlCommand(
                "SELECT tier_id FROM public.reader_tiers WHERE is_default = TRUE AND is_active = TRUE LIMIT 1;",
                connection, transaction))
            {
                object? value = await tierCmd.ExecuteScalarAsync(cancellationToken);
                if (value is null)
                    throw new InvalidOperationException("Chua co hang doc gia mac dinh (reader_tiers).");
                tierId = Convert.ToInt64(value, CultureInfo.InvariantCulture);
            }

            const string insertUser = @"
                INSERT INTO public.users (username, password_hash, role_id, account_status)
                VALUES (@username, @password_hash, @role_id, 'active')
                RETURNING user_id;";
            await using (var cmd = new NpgsqlCommand(insertUser, connection, transaction))
            {
                cmd.Parameters.Add("username", NpgsqlDbType.Varchar).Value = item.Username;
                cmd.Parameters.Add("password_hash", NpgsqlDbType.Text).Value = item.PasswordHash;
                cmd.Parameters.Add("role_id", NpgsqlDbType.Integer).Value = checked((int)roleId);
                userId = Convert.ToInt64(await cmd.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            }

            const string insertProfile = @"
                INSERT INTO public.reader_profiles
                  (user_id, reader_code, full_name, phone_e164, phone_verified_at, tier_id)
                VALUES
                  (@user_id, @reader_code, @full_name, @phone, CURRENT_TIMESTAMP, @tier_id);";
            await using (var cmd = new NpgsqlCommand(insertProfile, connection, transaction))
            {
                cmd.Parameters.Add("user_id", NpgsqlDbType.Bigint).Value = userId;
                cmd.Parameters.Add("reader_code", NpgsqlDbType.Varchar).Value = "RD" + userId.ToString("D10", CultureInfo.InvariantCulture);
                cmd.Parameters.Add("full_name", NpgsqlDbType.Varchar).Value = item.FullName;
                cmd.Parameters.Add("phone", NpgsqlDbType.Varchar).Value = item.Phone;
                cmd.Parameters.Add("tier_id", NpgsqlDbType.Bigint).Value = tierId;
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return new CompleteRegistrationResult(userId, item.Username, "Reader");
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw new RegistrationException("account_exists", "Ten dang nhap hoac so dien thoai da duoc su dung.");
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    // Only the Staff-issued 8-digit code is required. A valid code resolves exactly one
    // Guest under the server lock; no client-supplied guest identifier or phone lookup.
    public async Task<GuestProfilePreview> PreviewGuestAsync(
        string? guestClaimCode, CancellationToken cancellationToken = default)
    {
        EnsureDevelopment();
        guestClaimCode = (guestClaimCode ?? "").Trim();
        long guestUserId;
        lock (_sync)
        {
            guestUserId = ResolveGuestClaim(guestClaimCode, DateTimeOffset.UtcNow);
        }

        await using var connection = new NpgsqlConnection(GetConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(@"
            SELECT rp.full_name, COALESCE(rp.phone_e164,'')
              FROM public.users u
              JOIN public.reader_profiles rp ON rp.user_id=u.user_id
              JOIN public.roles r ON r.role_id=u.role_id
             WHERE u.user_id=@guest_id AND r.role_name='Reader'
               AND u.is_guest=TRUE AND u.account_status='active';", connection);
        cmd.Parameters.AddWithValue("guest_id", guestUserId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new RegistrationException("invalid_guest_claim", "Ma lien ket khong hop le hoac da het han.");
        // Never expose CCCD, fingerprint, loan history, or the Staff claim itself.
        return new GuestProfilePreview(reader.GetString(0), reader.GetString(1));
    }

    // Generated on a Staff-authenticated action after physical identity checking.
    // Only one active code per Guest, and codes must be unique ACROSS all active Guests.
    // Only HMAC hashes (not raw codes) live in RAM. API restart invalidates codes.
    public GuestClaimIssued IssueGuestClaim(long guestUserId, long staffUserId)
    {
        EnsureDevelopment();
        var now = DateTimeOffset.UtcNow;
        lock (_sync)
        {
            CleanupExpiredClaims(now);
            if (_guestClaims.TryGetValue(guestUserId, out var existing) && existing.ExpiresAt > now)
                throw new RegistrationException("claim_exists", "Dang co ma lien ket con han. Doi het han truoc khi cap ma moi.");
            string code;
            byte[] hash;
            do
            {
                code = RandomNumberGenerator.GetInt32(0, 100_000_000).ToString("D8", CultureInfo.InvariantCulture);
                hash = HashClaim(code);
            }
            while (_guestClaims.Values.Any(c => CryptographicOperations.FixedTimeEquals(c.Hash, hash)));

            var expiresAt = now.AddMinutes(10);
            _guestClaims[guestUserId] = new GuestClaim(hash, expiresAt, staffUserId);
            return new GuestClaimIssued(code, expiresAt);
        }
    }

    private byte[] HashClaim(string code)
    {
        using var hmac = new HMACSHA256(_otpSecret);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes("guest-claim-otp-only-v2:" + code));
    }

    // Called ONLY under _sync. Rate-limit unknown OTP guesses as well as valid-but-used
    // codes; otherwise an unauthenticated attacker could guess across many Guest IDs.
    private long ResolveGuestClaim(string code, DateTimeOffset now)
    {
        CleanupExpiredClaims(now);
        if (now >= _claimFailureWindowStartsAt.AddMinutes(1))
        {
            _claimFailureWindowStartsAt = now;
            _claimFailuresInWindow = 0;
        }
        if (_claimFailuresInWindow >= 10)
            throw new RegistrationException("guest_claim_unavailable", "Qua nhieu lan thu ma. Hay thu lai sau 1 phut.");
        if (!Regex.IsMatch(code, @"\A[0-9]{8}\z"))
        {
            _claimFailuresInWindow++;
            throw new RegistrationException("invalid_guest_claim", "Ma lien ket 8 so khong hop le hoac da het han.");
        }
        var hash = HashClaim(code);
        long matchingId = 0;
        // Traverse all active claims, rather than looking up a guessed Guest ID.
        foreach (var entry in _guestClaims)
        {
            if (CryptographicOperations.FixedTimeEquals(entry.Value.Hash, hash))
                matchingId = entry.Key;
        }
        if (matchingId == 0)
        {
            _claimFailuresInWindow++;
            throw new RegistrationException("invalid_guest_claim", "Ma lien ket 8 so khong hop le hoac da het han.");
        }
        var claim = _guestClaims[matchingId];
        if (claim.PendingRequestId.HasValue)
            throw new RegistrationException("guest_claim_unavailable", "Ma dang duoc su dung. Hay hoan tat OTP dang ky hoac xin cap lai.");
        return matchingId;
    }

    private void ReserveGuestClaim(long guestUserId, string code, Guid requestId, DateTimeOffset now)
    {
        if (ResolveGuestClaim(code, now) != guestUserId)
            throw new RegistrationException("invalid_guest_claim", "Ho so Guest khong khop voi ma lien ket.");
        _guestClaims[guestUserId].PendingRequestId = requestId;
    }

    private void CleanupExpiredClaims(DateTimeOffset now)
    {
        foreach (var id in _guestClaims.Where(x => x.Value.ExpiresAt <= now).Select(x => x.Key).ToArray())
            _guestClaims.Remove(id);
    }

    private sealed class GuestClaim
    {
        public GuestClaim(byte[] hash, DateTimeOffset expiresAt, long staffUserId)
        { Hash = hash; ExpiresAt = expiresAt; StaffUserId = staffUserId; }
        public byte[] Hash { get; }
        public DateTimeOffset ExpiresAt { get; }
        public long StaffUserId { get; }
        public Guid? PendingRequestId { get; set; }
    }

    private string GetConnectionString() =>
        _config.GetConnectionString("AivenPostgres")
        ?? throw new InvalidOperationException("Chua cau hinh AivenPostgres trong User Secrets.");

    private void EnsureDevelopment()
    {
        if (!_environment.IsDevelopment())
            throw new InvalidOperationException("Phai tich hop SMS/eKYC that truoc khi bat dang ky tren production.");
    }

    private byte[] ComputeOtpHash(Guid requestId, string otp)
    {
        using var hmac = new HMACSHA256(_otpSecret);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(requestId.ToString("N") + ":" + otp));
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach (var key in _pending.Where(x => x.Value.ExpiresAt <= now).Select(x => x.Key).ToArray())
            _pending.Remove(key);
    }

    private static string NormalizeVietnamesePhone(string? input)
    {
        string phone = Regex.Replace(input?.Trim() ?? "", @"[\s().-]", "");
        if (Regex.IsMatch(phone, @"\A0[35789]\d{8}\z"))
            return "+84" + phone.Substring(1);
        if (Regex.IsMatch(phone, @"\A\+84[35789]\d{8}\z"))
            return phone;
        throw new RegistrationException("invalid_phone", "So dien thoai di dong Viet Nam khong hop le.");
    }

    private sealed class PendingRegistration
    {
        public PendingRegistration(string fullName, string username, string phone,
            string passwordHash, byte[] otpHash, DateTimeOffset expiresAt, long guestUserId)
        {
            FullName = fullName;
            Username = username;
            Phone = phone;
            PasswordHash = passwordHash;
            OtpHash = otpHash;
            ExpiresAt = expiresAt;
            GuestUserId = guestUserId;
        }
        public long GuestUserId { get; }
        public string FullName { get; }
        public string Username { get; }
        public string Phone { get; }
        public string PasswordHash { get; }
        public byte[] OtpHash { get; }
        public DateTimeOffset ExpiresAt { get; }
        public int Attempts { get; set; }
    }
}

public sealed record GuestProfilePreview(string FullName, string Phone);
public sealed record GuestClaimIssued(string Code, DateTimeOffset ExpiresAt);
public sealed record StartRegistrationResult(Guid RequestId, DateTimeOffset ExpiresAt);
public sealed record CompleteRegistrationResult(long UserId, string Username, string Role);

public sealed class RegistrationException : Exception
{
    public RegistrationException(string code, string message) : base(message) => Code = code;
    public string Code { get; }
}
