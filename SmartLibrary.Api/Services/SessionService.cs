using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace SmartLibrary.Api.Services;

/// <summary>
/// Development-only in-memory session storage. Tokens are random, expire,
/// and are checked on the server. Restarting the API invalidates sessions.
/// For a public deployment, use a maintained identity provider/OIDC solution.
/// </summary>
public sealed class SessionService
{
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<string, SessionInfo> _sessions = new();

    public CreatedSession Create(long userId, string username, string role)
    {
        if (userId <= 0 || string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(role))
            throw new ArgumentException("Thong tin nguoi dung khong hop le.");

        byte[] bytes = RandomNumberGenerator.GetBytes(32);
        string token = Convert.ToBase64String(bytes)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        DateTimeOffset expiresAt = DateTimeOffset.UtcNow.Add(SessionLifetime);
        var info = new SessionInfo(userId, username, role, expiresAt);
        _sessions[HashToken(token)] = info;
        return new CreatedSession(token, expiresAt);
    }

    public SessionInfo? Validate(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 200)
            return null;

        string key = HashToken(token);
        if (!_sessions.TryGetValue(key, out SessionInfo? session))
            return null;

        if (session.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            _sessions.TryRemove(key, out _);
            return null;
        }

        return session;
    }

    public bool Revoke(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 200)
            return false;

        return _sessions.TryRemove(HashToken(token), out _);
    }

    private static string HashToken(string token)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(hash);
    }
}

public sealed record CreatedSession(string Token, DateTimeOffset ExpiresAt);
public sealed record SessionInfo(long UserId, string Username, string Role, DateTimeOffset ExpiresAt);
