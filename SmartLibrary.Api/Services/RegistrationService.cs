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

        if (string.IsNullOrWhiteSpace(password) || password.Length < 10 || password.Length > 128 ||
            !password.Any(char.IsLetter) || !password.Any(char.IsDigit))
            throw new RegistrationException("invalid_password", "Mat khau 10-128 ky tu, can co chu va so.");

        string connectionString = GetConnectionString();
        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync(cancellationToken);
            const string sql = @"
                SELECT
                  EXISTS (SELECT 1 FROM public.users WHERE LOWER(username) = LOWER(@username)),
                  EXISTS (SELECT 1 FROM public.reader_profiles WHERE phone_e164 = @phone);";
            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.Add("username", NpgsqlDbType.Varchar).Value = username;
            cmd.Parameters.Add("phone", NpgsqlDbType.Varchar).Value = normalizedPhone;
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            if (reader.GetBoolean(0) || reader.GetBoolean(1))
                throw new RegistrationException("account_exists", "Ten dang nhap hoac so dien thoai da duoc su dung.");
        }

        // Khong giu mat khau ro trong bo nho. BCrypt phai trung EnhancedVerify o AuthService.
        string passwordHash = BCrypt.Net.BCrypt.EnhancedHashPassword(password, workFactor: 12);
        Guid requestId = Guid.NewGuid();
        string otp = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var item = new PendingRegistration(
            fullName, username, normalizedPhone, passwordHash,
            ComputeOtpHash(requestId, otp), now.Add(OtpLifetime));

        lock (_sync)
        {
            RemoveExpired(now);
            if (_pending.Count >= 200 ||
                _pending.Values.Any(x =>
                    string.Equals(x.Phone, normalizedPhone, StringComparison.Ordinal) ||
                    string.Equals(x.Username, username, StringComparison.OrdinalIgnoreCase)))
                throw new RegistrationException("try_later", "Dang co yeu cau cho xu ly. Thu lai sau 5 phut.");
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
            // Lay khoi bo nho ngay khi OTP dung de ngan xac nhan lap lai.
            _pending.Remove(requestId);
        }

        await using var connection = new NpgsqlConnection(GetConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
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

            long userId;
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
            string passwordHash, byte[] otpHash, DateTimeOffset expiresAt)
        {
            FullName = fullName;
            Username = username;
            Phone = phone;
            PasswordHash = passwordHash;
            OtpHash = otpHash;
            ExpiresAt = expiresAt;
        }
        public string FullName { get; }
        public string Username { get; }
        public string Phone { get; }
        public string PasswordHash { get; }
        public byte[] OtpHash { get; }
        public DateTimeOffset ExpiresAt { get; }
        public int Attempts { get; set; }
    }
}

public sealed record StartRegistrationResult(Guid RequestId, DateTimeOffset ExpiresAt);
public sealed record CompleteRegistrationResult(long UserId, string Username, string Role);

public sealed class RegistrationException : Exception
{
    public RegistrationException(string code, string message) : base(message) => Code = code;
    public string Code { get; }
}
