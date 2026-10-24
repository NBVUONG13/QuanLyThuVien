
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls("http://127.0.0.1:5082");

var app = builder.Build();

app.MapGet("/api/health", async (
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    string? connectionString =
        configuration.GetConnectionString("AivenPostgres");

    if (string.IsNullOrWhiteSpace(connectionString))
    {
        return Results.Json(new
        {
            status = "error",
            service = "SmartLibrary.Api",
            database = "not_configured"
        }, statusCode: 503);
    }

    try
    {
        await using var connection =
            new NpgsqlConnection(connectionString);

        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            "SELECT COUNT(*) FROM public.books;",
            connection);

        var result =
            await command.ExecuteScalarAsync(cancellationToken);

        long bookCount = Convert.ToInt64(result);

        return Results.Ok(new
        {
            status = "ok",
            service = "SmartLibrary.Api",
            database = "connected",
            books = bookCount
        });
    }
    catch (Exception exception)
    {
        app.Logger.LogWarning(
            "PostgreSQL connection failed: {ErrorType}",
            exception.GetType().Name);

        return Results.Json(new
        {
            status = "error",
            service = "SmartLibrary.Api",
            database = "connection_failed",
            errorType = exception.GetType().Name
        }, statusCode: 503);
    }
});

app.Run();
