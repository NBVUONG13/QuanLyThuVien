using Microsoft.Extensions.Configuration;
using Npgsql;
using System;
using System.Configuration;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace SmartLibrary.Api.Services;

/// <summary>One-time local bootstrap. Not an HTTP endpoint. Never call from a public request.</summary>
public static class AdminProvisioning
{
    public static async Task RunBootstrapAsync(IConfiguration config, CancellationToken ct = default)
    {
        string username = (config["BootstrapAdmin:Username"] ?? "").Trim();
        string fullname = (config["BootstrapAdmin:FullName"] ?? "").Trim();
        string code = (config["BootstrapAdmin:StaffCode"] ?? "").Trim();
        string password = config["BootstrapAdmin:Password"] ?? "";
        if (username.Length < 3 || username.Length > 40 ||
            !System.Text.RegularExpressions.Regex.IsMatch(username, @"\A[a-zA-Z0-9._]{3,40}\z") ||
            fullname.Length < 2 || fullname.Length > 200 ||
            code.Length < 2 || code.Length > 40 ||
            password.Length < 5 || password.Length > 128)
            throw new InvalidOperationException("BootstrapAdmin secrets invalid. Username 3-40, full name 2-200, staff code 2-40, password 5-128 characters.");
        string cs = config.GetConnectionString("AivenPostgres")
            ?? throw new InvalidOperationException("AivenPostgres User Secret missing.");
        await using var db = new NpgsqlConnection(cs);
        await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        // Transaction-scoped lock serializes bootstrap with itself across processes.
        await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(91736124)", db, tx))
            await lockCmd.ExecuteNonQueryAsync(ct);
        await using (var check = new NpgsqlCommand(@"
            SELECT COUNT(*) FROM public.users u JOIN public.roles r ON r.role_id=u.role_id
            WHERE LOWER(r.role_name)='admin'", db, tx))
        {
            if (Convert.ToInt64(await check.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) > 0)
                throw new InvalidOperationException("Admin already exists. Bootstrap refused; no changes saved.");
        }
        await using (var check = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM public.users WHERE LOWER(username)=LOWER(@u))", db, tx))
        {
            check.Parameters.AddWithValue("u", username);
            if (Convert.ToBoolean(await check.ExecuteScalarAsync(ct)))
                throw new InvalidOperationException("Username already exists; no changes saved.");
        }
        long roleId;
        await using (var role = new NpgsqlCommand("SELECT role_id FROM public.roles WHERE LOWER(role_name)='admin' LIMIT 1", db, tx))
            roleId = Convert.ToInt64(await role.ExecuteScalarAsync(ct) ?? throw new InvalidOperationException("Admin role missing."));
        string hash = BCrypt.Net.BCrypt.EnhancedHashPassword(password, workFactor: 12);
        long userId;
        await using (var insert = new NpgsqlCommand(@"
            INSERT INTO public.users(username,password_hash,role_id,account_status)
            VALUES(@u,@h,@r,'active') RETURNING user_id", db, tx))
        {
            insert.Parameters.AddWithValue("u", username);
            insert.Parameters.AddWithValue("h", hash);
            insert.Parameters.AddWithValue("r", checked((int)roleId));
            userId = Convert.ToInt64(await insert.ExecuteScalarAsync(ct));
        }
        await using (var profile = new NpgsqlCommand(@"
            INSERT INTO public.staff_profiles(user_id,staff_code,full_name,hourly_rate)
            VALUES(@id,@code,@full,0)", db, tx))
        {
            profile.Parameters.AddWithValue("id", userId);
            profile.Parameters.AddWithValue("code", code);
            profile.Parameters.AddWithValue("full", fullname);
            await profile.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        Console.WriteLine("Admin bootstrap succeeded. User ID: " + userId + ". Delete BootstrapAdmin secrets now.");
    }
}
