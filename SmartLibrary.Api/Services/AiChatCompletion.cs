using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace SmartLibrary.Api.Services;

/// <summary>
/// Chat-only provider router for the backend: local LM Studio if it lives on the
/// SAME computer as the API; otherwise Gemini via HTTPS with server-held key.
/// Do not connect to user-supplied URLs or expose Gemini:ApiKey in desktop config.
/// </summary>
internal static class AiChatCompletion
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(35)
    };
    private const string GeminiChatEndpoint =
        "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions";

    internal static async Task<string?> AskAsync(IConfiguration configuration,
        object[] messages, double temperature, int maxTokens, CancellationToken cancellationToken)
    {
        // 1. Local model on the backend computer (useful for localhost Development).
        var local = await FindLocalAsync(configuration, cancellationToken);
        if (local.HasValue)
        {
            string? answer = await CompleteAsync(local.Value.Url, local.Value.Model,
                null, messages, temperature, maxTokens, cancellationToken);
            if (!string.IsNullOrWhiteSpace(answer)) return answer;
        }

        // 2. Gemini on Render / any computer without a local LLM.
        string? key = configuration["Gemini:ApiKey"];
        if (string.IsNullOrWhiteSpace(key)) return null;
        string model = configuration["Gemini:Model"] ?? "gemini-3.5-flash-lite";
        if (!Regex.IsMatch(model, @"^[A-Za-z0-9._-]{2,80}$"))
            model = "gemini-3.5-flash-lite";
        return await CompleteAsync(GeminiChatEndpoint, model, key.Trim(),
            messages, temperature, maxTokens, cancellationToken);
    }

    private static async Task<(string Url, string Model)?> FindLocalAsync(
        IConfiguration configuration, CancellationToken cancellationToken)
    {
        string rawUrl = configuration["LMStudio:BaseUrl"] ?? "http://127.0.0.1:1234";
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme != Uri.UriSchemeHttp || !uri.IsLoopback ||
            uri.Port is < 1 or > 65535 || !string.IsNullOrEmpty(uri.UserInfo)) return null;
        string host = uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        try
        {
            // Small timeout when LM Studio is not installed, e.g. on Render.
            using var probe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            probe.CancelAfter(TimeSpan.FromMilliseconds(1100));
            using var result = await Http.GetAsync(host + "/v1/models", probe.Token);
            if (!result.IsSuccessStatusCode) return null;
            using var data = JsonDocument.Parse(await result.Content.ReadAsStringAsync(probe.Token));
            if (!data.RootElement.TryGetProperty("data", out var rows) ||
                rows.ValueKind != JsonValueKind.Array) return null;
            var available = rows.EnumerateArray()
                .Select(x => x.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                    ? id.GetString() : null)
                .Where(x => !string.IsNullOrWhiteSpace(x) &&
                    !x.Contains("embedding", StringComparison.OrdinalIgnoreCase) &&
                    !x.Contains("rerank", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            string? requested = configuration["LMStudio:Model"];
            string? model = available.FirstOrDefault(x =>
                    string.Equals(x, requested, StringComparison.OrdinalIgnoreCase))
                ?? available.OrderByDescending(x => x!.Contains("qwen3.5", StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault();
            return string.IsNullOrWhiteSpace(model) ? null
                : (host + "/v1/chat/completions", model);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (HttpRequestException) { return null; }
        catch (JsonException) { return null; }
    }

    private static async Task<string?> CompleteAsync(string url, string model, string? geminiKey,
        object[] messages, double temperature, int maxTokens, CancellationToken cancellationToken)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            using var message = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(new
                {
                    model,
                    messages,
                    temperature,
                    // Gemini thinking tokens may count against the completion budget.
                    max_tokens = geminiKey == null ? maxTokens : Math.Max(maxTokens, 1600),
                    stream = false
                })
            };
            if (geminiKey != null)
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", geminiKey);
            using var result = await Http.SendAsync(message, deadline.Token);
            // Never log the response body, key, questions or token.
            if (!result.IsSuccessStatusCode) return null;
            using var content = JsonDocument.Parse(await result.Content.ReadAsStringAsync(deadline.Token));
            if (!content.RootElement.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0) return null;
            var first = choices[0];
            if (!first.TryGetProperty("message", out var reply) ||
                !reply.TryGetProperty("content", out var text) || text.ValueKind != JsonValueKind.String)
                return null;
            string? answer = text.GetString()?.Trim();
            return string.IsNullOrWhiteSpace(answer) ? null : answer;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (HttpRequestException) { return null; }
        catch (JsonException) { return null; }
    }
}
