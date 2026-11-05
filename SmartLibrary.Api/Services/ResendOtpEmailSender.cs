using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SmartLibrary.Api.Services;

/// <summary>
/// Compatibility name retained because Program.cs and EmailRegistrationService.cs
/// already depend on ResendOtpEmailSender. This class NO LONGER USES RESEND.
/// It securely submits an HMAC-signed email job to Google Apps Script over HTTPS.
/// The Apps Script account sends mail using Google's MailApp service.
/// </summary>
public sealed class ResendOtpEmailSender
{
    private readonly HttpClient _http;
    private readonly IConfiguration _config;
    private readonly ILogger<ResendOtpEmailSender> _logger;

    public ResendOtpEmailSender(HttpClient http, IConfiguration config,
        ILogger<ResendOtpEmailSender> logger)
    {
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(60);
        _config = config;
        _logger = logger;
    }

    public async Task SendRegistrationCodeAsync(string toEmail, string otp, CancellationToken ct)
    {
        string? scriptUrl = _config["EmailOtp:AppsScriptUrl"]?.Trim();
        string? sharedSecret = _config["EmailOtp:AppsScriptSecret"];

        if (!IsValidAppsScriptUrl(scriptUrl) || string.IsNullOrEmpty(sharedSecret) ||
            sharedSecret.Length < 32)
        {
            _logger.LogError("Gmail OTP configuration invalid or missing on API server.");
            throw new EmailOtpDeliveryException(
                "Server chưa cấu hình Google Apps Script OTP hợp lệ. Hãy kiểm tra Render Environment.");
        }

        // EmailRegistrationService already validates the user email. Check again at the
        // transport boundary so we never send arbitrary, unsigned payloads.
        string recipient = (toEmail ?? "").Trim().ToLowerInvariant();
        if (recipient.Length is < 3 or > 254 || recipient.Contains('\n') ||
            recipient.Contains('\r') || recipient.Contains(' ') ||
            !recipient.Contains('@') || !Regex.IsMatch(otp ?? "", @"\A[0-9]{6}\z"))
        {
            throw new EmailOtpDeliveryException("Địa chỉ email hoặc mã OTP không hợp lệ.");
        }

        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        string signedText = timestamp.ToString(CultureInfo.InvariantCulture) + "\n" +
                            nonce + "\n" + recipient + "\n" + otp;
        byte[] signatureBytes;
        using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(sharedSecret)))
        {
            signatureBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(signedText));
        }

        string payload = JsonSerializer.Serialize(new
        {
            timestamp,
            nonce,
            to = recipient,
            otp,
            signature = Convert.ToBase64String(signatureBytes)
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, scriptUrl);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        try
        {
            // Google ContentService redirects to script.googleusercontent.com.
            // HttpClient follows those redirects by default.
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Google Apps Script OTP transport returned HTTP {Status}",
                    (int)response.StatusCode);
                throw new EmailOtpDeliveryException(
                    "Không gọi được Google Apps Script gửi OTP. Kiểm tra Web app /exec và quyền Anyone.");
            }

            string reply = await response.Content.ReadAsStringAsync(ct);
            try
            {
                using var parsed = JsonDocument.Parse(reply);
                var root = parsed.RootElement;
                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
                    return;

                string reason = root.ValueKind == JsonValueKind.Object &&
                                root.TryGetProperty("error", out var error) &&
                                error.ValueKind == JsonValueKind.String
                    ? error.GetString() ?? "unknown"
                    : "unknown";
                // Never log the HTTP body: it may include PII.
                // Limit the log to a known, server-generated diagnostic category.
                string safeReason = reason switch
                {
                    "invalid_request" or "invalid_signature" or "expired_request" or
                    "replayed_request" or "quota_exceeded" or "mail_send_failed" or
                    "script_secret_missing" or "busy" => reason,
                    _ => "unknown"
                };
                _logger.LogWarning("Google Apps Script OTP request refused: {Reason}", safeReason);
                throw new EmailOtpDeliveryException(safeReason switch
                {
                    "quota_exceeded" => "Gmail đã đạt hạn mức gửi email hôm nay. Thử lại sau.",
                    "script_secret_missing" or "invalid_signature" =>
                        "Khóa bảo mật Apps Script chưa khớp với Render.",
                    "mail_send_failed" => "Gmail chưa gửi được OTP. Kiểm tra quyền MailApp và Google Apps Script Executions.",
                    _ => "Không gửi được OTP qua Gmail. Kiểm tra cấu hình và nhật ký Apps Script."
                });
            }
            catch (JsonException)
            {
                // A Google login/permissions page may return HTML with HTTP 200.
                _logger.LogWarning("Apps Script returned non-JSON response. Check web app deployment/permissions.");
                throw new EmailOtpDeliveryException(
                    "Google Apps Script không trả JSON. Kiểm tra Deploy as Me, Access Anyone và URL /exec.");
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("Google Apps Script network failure: {Type}", ex.GetType().Name);
            throw new EmailOtpDeliveryException("Không kết nối được Google Apps Script. Thử lại sau.");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Google Apps Script email request timed out.");
            throw new EmailOtpDeliveryException("Gmail phản hồi chậm hoặc hết thời gian. Thử lại sau.");
        }
    }

    private static bool IsValidAppsScriptUrl(string? url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
               uri.Scheme == Uri.UriSchemeHttps &&
               string.Equals(uri.Host, "script.google.com", StringComparison.OrdinalIgnoreCase) &&
               uri.AbsolutePath.StartsWith("/macros/s/", StringComparison.Ordinal) &&
               uri.AbsolutePath.EndsWith("/exec", StringComparison.Ordinal) &&
               string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);
    }
}

// Preserve the existing exception used by EmailRegistrationEndpoints.cs.
public sealed class EmailOtpDeliveryException : Exception
{
    public EmailOtpDeliveryException(string message) : base(message) { }
}
