using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace SmartLibrary.Api.Services;

/// <summary>
/// Production: random opaque Bearer tokens, stored hashed in the existing
/// Aiven PostgreSQL database; survives Render restarts and supports logout.
/// Development: old in-memory implementation, so localhost is unaffected.
/// </summary>
public sealed class SessionService
{
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(8);
    private readonly ConcurrentDictionary<string, SessionInfo> _local = new();
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;
    private readonly IHttpContextAccessor _http;
    private readonly ILogger<SessionService> _logger;

    public SessionService(IConfiguration configuration, IHostEnvironment environment,
        IHttpContextAccessor http, ILogger<SessionService> logger)
    {
        _configuration = configuration;
        _environment = environment;
        _http = http;
        _logger = logger;
    }

    public CreatedSession Create(long userId, string username, string role)
    {
        if (userId <= 0 || string.IsNullOrWhiteSpace(username) ||
            role is not ("Reader" or "Staff" or "Admin"))
            throw new ArgumentException("Invalid user identity.");

        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        DateTimeOffset expiresAt = DateTimeOffset.UtcNow.Add(SessionLifetime);
        string hash = HashToken(token);

        if (_environment.IsDevelopment())
        {
            _local[hash] = new SessionInfo(userId, username, role, expiresAt);
        }
        else
        {
            using var db = new NpgsqlConnection(ConnectionString());
            db.Open();
            using var cmd = new NpgsqlCommand(@"
                INSERT INTO public.api_sessions (token_hash, user_id, expires_at)
                SELECT @hash, u.user_id, @expiry
                  FROM public.users u
                 WHERE u.user_id = @user_id AND u.account_status = 'active'
                   AND u.is_guest = FALSE;", db);
            cmd.Parameters.AddWithValue("hash", hash);
            cmd.Parameters.AddWithValue("user_id", userId);
            cmd.Parameters.AddWithValue("expiry", expiresAt.UtcDateTime);
            if (cmd.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("Cannot issue token for inactive account.");
        }
        return new CreatedSession(token, expiresAt);
    }

    public SessionInfo? Validate(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 200)
            return null;
        string hash = HashToken(token);
        var ctx = _http.HttpContext;
        var cacheKey = "SmartLibrary.Session:" + hash;
        if (ctx is not null && ctx.Items.TryGetValue(cacheKey, out var cached))
            return cached as SessionInfo;

        SessionInfo? result = null;
        if (_environment.IsDevelopment())
        {
            if (_local.TryGetValue(hash, out var local))
            {
                if (local.ExpiresAt > DateTimeOffset.UtcNow) result = local;
                else _local.TryRemove(hash, out _);
            }
        }
        else
        {
            try
            {
                using var db = new NpgsqlConnection(ConnectionString());
                db.Open();
                using var cmd = new NpgsqlCommand(@"
                    SELECT u.user_id, u.username, r.role_name, s.expires_at
                      FROM public.api_sessions s
                      JOIN public.users u ON u.user_id = s.user_id
                      JOIN public.roles r ON r.role_id = u.role_id
                     WHERE s.token_hash = @hash
                       AND s.revoked_at IS NULL
                       AND s.expires_at > NOW()
                       AND u.account_status = 'active'
                       AND u.is_guest = FALSE
                     LIMIT 1;", db);
                cmd.Parameters.AddWithValue("hash", hash);
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    string currentRole = reader.GetString(2);
                    if (currentRole is "Reader" or "Staff" or "Admin")
                        result = new SessionInfo(reader.GetInt64(0), reader.GetString(1),
                            currentRole, new DateTimeOffset(reader.GetDateTime(3).ToUniversalTime()));
                }
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
            {
                // Fail closed, never dump DB strings / tokens.
                _logger.LogWarning("Session validation failed: {Type}", ex.GetType().Name);
            }
        }
        if (ctx is not null) ctx.Items[cacheKey] = result!;
        return result;
    }

    public bool Revoke(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 200)
            return false;
        string hash = HashToken(token);
        if (_environment.IsDevelopment()) return _local.TryRemove(hash, out _);
        using var db = new NpgsqlConnection(ConnectionString());
        db.Open();
        using var cmd = new NpgsqlCommand(@"
            UPDATE public.api_sessions SET revoked_at = NOW()
             WHERE token_hash = @hash AND revoked_at IS NULL;", db);
        cmd.Parameters.AddWithValue("hash", hash);
        return cmd.ExecuteNonQuery() == 1;
    }

    private string ConnectionString() =>
        _configuration.GetConnectionString("AivenPostgres") ??
        throw new InvalidOperationException("AivenPostgres connection string is not configured.");

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

public sealed record CreatedSession(string Token, DateTimeOffset ExpiresAt);
public sealed record SessionInfo(long UserId, string Username, string Role, DateTimeOffset ExpiresAt);
