using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;

namespace SmartLibrary.Api.Services;

/// <summary>
/// Cloud-safe Reader self-registration: Resend email OTP, Aiven-backed pending challenge,
/// fixed-time HMAC verification, strict attempt limits. No console OTP and no guest claim.
/// Email verification is NOT phone-number verification.
/// </summary>
public sealed class EmailRegistrationService
{
    private readonly IConfiguration _config;
    private readonly ResendOtpEmailSender _sender;
    private readonly ILogger<EmailRegistrationService> _logger;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    public EmailRegistrationService(IConfiguration config, ResendOtpEmailSender sender,
        ILogger<EmailRegistrationService> logger)
    {
        _config = config;
        _sender = sender;
        _logger = logger;
    }

    public async Task<EmailRegistrationStarted> StartAsync(EmailRegisterStart request, CancellationToken ct)
    {
        string fullName = request.FullName?.Trim() ?? "";
        string username = request.Username?.Trim() ?? "";
        string email = (request.Email ?? "").Trim().ToLowerInvariant();
        string phone = NormalizePhone(request.Phone);
        string password = request.Password ?? "";

        if (!string.IsNullOrWhiteSpace(request.GuestClaimCode))
            throw new EmailRegistrationException("guest_requires_staff",
                "Lien ket tai khoan Guest can xac minh danh tinh tai quay; chua ho tro qua email.");
        if (fullName.Length is < 2 or > 200)
            throw new EmailRegistrationException("invalid_name", "Ho ten phai co 2-200 ky tu.");
        if (!Regex.IsMatch(username, @"\A[a-zA-Z0-9._]{3,40}\z") ||
            Regex.IsMatch(username, @"\Aguest\d+\z", RegexOptions.IgnoreCase))
            throw new EmailRegistrationException("invalid_username", "Ten tai khoan khong hop le hoac da duoc danh rieng cho Guest.");
        if (email.Length > 254 || !Regex.IsMatch(email,
                @"\A[A-Za-z0-9.!#$%&'*+/=?^_`{|}~-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)+\z"))
            throw new EmailRegistrationException("invalid_email", "Email khong hop le.");
        if (password.Length is < 10 or > 128 || !password.Any(char.IsLetter) || !password.Any(char.IsDigit))
            throw new EmailRegistrationException("invalid_password", "Mat khau can 10-128 ky tu, co chu va so.");

        Guid id = Guid.NewGuid();
        string otp = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        DateTimeOffset expiresAt = DateTimeOffset.UtcNow.Add(Lifetime);
        byte[] hash = ComputeOtpHash(id, otp);
        string passwordHash = BCrypt.Net.BCrypt.EnhancedHashPassword(password, workFactor: 12);

        await using (var db = new NpgsqlConnection(ConnectionString()))
        {
            await db.OpenAsync(ct);
            await using var tx = await db.BeginTransactionAsync(ct);
            // Reap only expired challenges; a pending one prevents repeated emails/DoS.
            await using (var cleanup = new NpgsqlCommand(
                "DELETE FROM public.email_registration_challenges WHERE expires_at <= now();", db, tx))
                await cleanup.ExecuteNonQueryAsync(ct);

            await using (var duplicate = new NpgsqlCommand(@"
                SELECT EXISTS(SELECT 1 FROM public.users WHERE lower(username)=lower(@u))
                    OR EXISTS(SELECT 1 FROM public.reader_profiles
                               WHERE phone_e164=@p OR lower(email)=@e);", db, tx))
            {
                duplicate.Parameters.AddWithValue("u", username);
                duplicate.Parameters.AddWithValue("p", phone);
                duplicate.Parameters.AddWithValue("e", email);
                if (Convert.ToBoolean(await duplicate.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture))
                    throw new EmailRegistrationException("account_exists",
                        "Tai khoan, so dien thoai hoac email da ton tai. Neu da co ho so Guest, hay lien he Staff.");
            }
            try
            {
                await using var cmd = new NpgsqlCommand(@"
                    INSERT INTO public.email_registration_challenges
                      (request_id,username,full_name,phone_e164,email,password_hash,otp_hash,expires_at)
                    VALUES (@id,@u,@n,@p,@e,@h,@otp,@exp);", db, tx);
                cmd.Parameters.AddWithValue("id", id);
                cmd.Parameters.AddWithValue("u", username);
                cmd.Parameters.AddWithValue("n", fullName);
                cmd.Parameters.AddWithValue("p", phone);
                cmd.Parameters.AddWithValue("e", email);
                cmd.Parameters.AddWithValue("h", passwordHash);
                cmd.Parameters.Add("otp", NpgsqlDbType.Bytea).Value = hash;
                cmd.Parameters.AddWithValue("exp", expiresAt);
                await cmd.ExecuteNonQueryAsync(ct);
                await tx.CommitAsync(ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                throw new EmailRegistrationException("try_later",
                    "Da co yeu cau OTP cho tai khoan/email/so dien thoai nay. Hay kiem tra email hoac thu lai sau 5 phut.");
            }
        }

        try
        {
            await _sender.SendRegistrationCodeAsync(email, otp, ct);
            return new EmailRegistrationStarted(id, expiresAt);
        }
        catch
        {
            // Do not leave a pending registration that prevents retry if mail delivery failed.
            try
            {
                await using var db = new NpgsqlConnection(ConnectionString());
                await db.OpenAsync(CancellationToken.None);
                await using var cmd = new NpgsqlCommand(
                    "DELETE FROM public.email_registration_challenges WHERE request_id=@id;", db);
                cmd.Parameters.AddWithValue("id", id);
                await cmd.ExecuteNonQueryAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Unable to clean up failed email challenge: {Type}", ex.GetType().Name);
            }
            throw;
        }
    }

    public async Task<EmailRegistrationCompleted> ConfirmAsync(EmailRegisterConfirm request, CancellationToken ct)
    {
        if (request.RequestId == Guid.Empty || !Regex.IsMatch(request.Otp ?? "", @"\A\d{6}\z"))
            throw new EmailRegistrationException("invalid_otp", "Ma OTP phai gom dung 6 chu so.");

        await using var db = new NpgsqlConnection(ConnectionString());
        await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        string username, fullName, phone, email, passwordHash;
        byte[] savedHash;
        DateTimeOffset expiresAt;
        int attempts;
        await using (var cmd = new NpgsqlCommand(@"
            SELECT username,full_name,phone_e164,email,password_hash,otp_hash,expires_at,attempts
              FROM public.email_registration_challenges
             WHERE request_id=@id FOR UPDATE;", db, tx))
        {
            cmd.Parameters.AddWithValue("id", request.RequestId);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct))
                throw new EmailRegistrationException("invalid_otp", "Ma OTP khong hop le hoac da het han.");
            username = rd.GetString(0);
            fullName = rd.GetString(1);
            phone = rd.GetString(2);
            email = rd.GetString(3);
            passwordHash = rd.GetString(4);
            savedHash = (byte[])rd.GetValue(5);
            expiresAt = rd.GetFieldValue<DateTimeOffset>(6);
            attempts = rd.GetInt32(7);
        }
        if (expiresAt <= DateTimeOffset.UtcNow || attempts >= 5)
            throw new EmailRegistrationException("invalid_otp", "Ma OTP da het han. Vui long tao yeu cau moi.");
        if (!CryptographicOperations.FixedTimeEquals(savedHash, ComputeOtpHash(request.RequestId, request.Otp!)))
        {
            await using var increment = new NpgsqlCommand(@"
                UPDATE public.email_registration_challenges SET attempts=attempts+1
                 WHERE request_id=@id;", db, tx);
            increment.Parameters.AddWithValue("id", request.RequestId);
            await increment.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
            throw new EmailRegistrationException("invalid_otp", "Ma OTP sai. Toi da 5 lan nhap.");
        }

        try
        {
            long roleId, tierId;
            await using (var role = new NpgsqlCommand(
                "SELECT role_id FROM public.roles WHERE role_name='Reader' LIMIT 1;", db, tx))
                roleId = Convert.ToInt64(await role.ExecuteScalarAsync(ct)
                    ?? throw new InvalidOperationException("Missing Reader role"), CultureInfo.InvariantCulture);
            await using (var tier = new NpgsqlCommand(@"
                SELECT tier_id FROM public.reader_tiers
                 WHERE is_default=TRUE AND is_active=TRUE LIMIT 1;", db, tx))
                tierId = Convert.ToInt64(await tier.ExecuteScalarAsync(ct)
                    ?? throw new InvalidOperationException("Missing default Reader tier"), CultureInfo.InvariantCulture);

            long userId;
            await using (var user = new NpgsqlCommand(@"
                INSERT INTO public.users (username,password_hash,role_id,account_status)
                VALUES (@u,@hash,@role,'active') RETURNING user_id;", db, tx))
            {
                user.Parameters.AddWithValue("u", username);
                user.Parameters.AddWithValue("hash", passwordHash);
                user.Parameters.Add("role", NpgsqlDbType.Integer).Value = checked((int)roleId);
                userId = Convert.ToInt64(await user.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
            }
            await using (var profile = new NpgsqlCommand(@"
                INSERT INTO public.reader_profiles
                  (user_id,reader_code,full_name,phone_e164,phone_verified_at,
                   email,email_verified_at,tier_id)
                VALUES (@id,@code,@name,@phone,NULL,@email,now(),@tier);", db, tx))
            {
                profile.Parameters.AddWithValue("id", userId);
                profile.Parameters.AddWithValue("code", "RD" + userId.ToString("D10", CultureInfo.InvariantCulture));
                profile.Parameters.AddWithValue("name", fullName);
                profile.Parameters.AddWithValue("phone", phone);
                profile.Parameters.AddWithValue("email", email);
                profile.Parameters.AddWithValue("tier", NpgsqlDbType.Bigint).Value = tierId;
                await profile.ExecuteNonQueryAsync(ct);
            }
            await using (var delete = new NpgsqlCommand(@"
                DELETE FROM public.email_registration_challenges WHERE request_id=@id;", db, tx))
            {
                delete.Parameters.AddWithValue("id", request.RequestId);
                await delete.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
            return new EmailRegistrationCompleted(userId, username, "Reader");
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new EmailRegistrationException("account_exists",
                "Tai khoan, email hoac so dien thoai nay da duoc su dung.");
        }
    }

    private string ConnectionString() => _config.GetConnectionString("AivenPostgres")
        ?? throw new InvalidOperationException("Missing Aiven PostgreSQL connection string");

    private byte[] ComputeOtpHash(Guid id, string code)
    {
        string pepper = _config["EmailOtp:Pepper"] ?? "";
        if (pepper.Length < 32)
            throw new InvalidOperationException("EmailOtp:Pepper must contain at least 32 characters");
        byte[] secret = SHA256.HashData(Encoding.UTF8.GetBytes(pepper));
        using var hmac = new HMACSHA256(secret);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(id.ToString("N") + ":" + code));
    }

    private static string NormalizePhone(string? input)
    {
        string p = Regex.Replace(input?.Trim() ?? "", @"[\s().-]", "");
        if (Regex.IsMatch(p, @"\A0[35789]\d{8}\z")) return "+84" + p[1..];
        if (Regex.IsMatch(p, @"\A\+84[35789]\d{8}\z")) return p;
        throw new EmailRegistrationException("invalid_phone", "So dien thoai di dong Viet Nam khong hop le.");
    }
}

public sealed record EmailRegisterStart(string? FullName, string? Username, string? Phone,
    string? Email, string? Password, string? GuestClaimCode);
public sealed record EmailRegisterConfirm(Guid RequestId, string? Otp);
public sealed record EmailRegistrationStarted(Guid RequestId, DateTimeOffset ExpiresAt);
public sealed record EmailRegistrationCompleted(long UserId, string Username, string Role);

public sealed class EmailRegistrationException : Exception
{
    public string Code { get; }
    public EmailRegistrationException(string code, string message) : base(message) => Code = code;
}
