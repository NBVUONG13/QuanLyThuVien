
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NpgsqlTypes;

namespace SmartLibrary.Api.Services;

public sealed class AuthService
{
    private readonly IConfiguration _configuration;

    public AuthService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public async Task<AuthenticatedUser?> LoginAsync(
        string? username,
        string? password,
        CancellationToken cancellationToken = default)
    {
        username = username?.Trim();

        if (string.IsNullOrWhiteSpace(username) ||
            string.IsNullOrEmpty(password) ||
            username.Length > 100 ||
            password.Length > 128)
        {
            return null;
        }

        string connectionString =
            _configuration.GetConnectionString("AivenPostgres")
            ?? throw new InvalidOperationException(
                "Chua cau hinh AivenPostgres trong User Secrets.");

        await using var connection =
            new NpgsqlConnection(connectionString);

        await connection.OpenAsync(cancellationToken);

        const string sql = @"
            SELECT
                u.user_id,
                u.username,
                u.password_hash,
                u.account_status,
                r.role_name
            FROM public.users AS u
            JOIN public.roles AS r
                ON r.role_id = u.role_id
            WHERE LOWER(u.username) = LOWER(@username)
            LIMIT 1;";

        await using var command =
            new NpgsqlCommand(sql, connection);

        command.Parameters.Add(
            "username", NpgsqlDbType.Varchar
        ).Value = username;

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
            return null;

        if (reader.IsDBNull(2) || reader.IsDBNull(3))
            return null;

        long userId = reader.GetInt64(0);
        string savedUsername = reader.GetString(1);
        string savedHash = reader.GetString(2);
        string accountStatus = reader.GetString(3);
        string role = reader.GetString(4);

        bool passwordMatches;

        try
        {
            passwordMatches =
                BCrypt.Net.BCrypt.EnhancedVerify(
                    password, savedHash);
        }
        catch (BCrypt.Net.SaltParseException)
        {
            return null;
        }

        if (!passwordMatches ||
            !string.Equals(
                accountStatus,
                "active",
                StringComparison.Ordinal))
        {
            return null;
        }

        return new AuthenticatedUser(
            userId, savedUsername, role);
    }
}

public sealed record AuthenticatedUser(
    long UserId,
    string Username,
    string Role
);
