using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SmartLibrary.Api.Services;

/// <summary>Send a registration OTP using Resend HTTPS; secrets stay on the API server.</summary>
public sealed class ResendOtpEmailSender
{
    private readonly HttpClient _http;
    private readonly IConfiguration _config;
    private readonly ILogger<ResendOtpEmailSender> _logger;

    public ResendOtpEmailSender(HttpClient http, IConfiguration config,
        ILogger<ResendOtpEmailSender> logger)
    {
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(25);
        _config = config;
        _logger = logger;
    }

    public async Task SendRegistrationCodeAsync(string toEmail, string otp, CancellationToken ct)
    {
        string? apiKey = _config["EmailOtp:ResendApiKey"];
        string? sender = _config["EmailOtp:FromEmail"];
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(sender))
            throw new EmailOtpDeliveryException("Chua cau hinh Resend API Key/FromEmail tren server.");

        var payload = new
        {
            from = sender,
            to = new[] { toEmail },
            subject = "Smart Library - Ma xac minh dang ky",
            text = "Ma xac minh dang ky Smart Library cua ban: " + otp +
                   "\nMa co hieu luc trong 5 phut. Khong chia se ma nay cho bat ky ai." +
                   "\nNeu ban khong yeu cau dang ky, hay bo qua email nay."
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.resend.com/emails");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        try
        {
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                // Never log the response body: it may include email/PII.
                _logger.LogWarning("Resend OTP delivery failed: HTTP {Status}", (int)response.StatusCode);
                throw new EmailOtpDeliveryException(
                    "Khong gui duoc email OTP. Kiem tra Resend API key va ten mien nguoi gui da xac minh.");
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("Resend network failure: {Type}", ex.GetType().Name);
            throw new EmailOtpDeliveryException("Khong ket noi duoc dich vu email. Hay thu lai sau.");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new EmailOtpDeliveryException("Dich vu email phan hoi cham. Hay thu lai sau.");
        }
    }
}

public sealed class EmailOtpDeliveryException : Exception
{
    public EmailOtpDeliveryException(string message) : base(message) { }
}
