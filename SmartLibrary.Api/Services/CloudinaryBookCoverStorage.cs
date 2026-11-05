using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SmartLibrary.Api.Services;

// Cloudinary authenticated API client. API key/secret never leave the backend.
// REST + HTTPS is enough; no Cloudinary SDK / AWS package is required.
public sealed class CloudinaryBookCoverStorage : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };
    private static readonly Regex CloudNamePattern = new(@"\A[a-zA-Z0-9_-]{2,100}\z", RegexOptions.Compiled);
    private static readonly Regex PublicIdPattern = new(@"\Asmartlibrary/covers/[a-zA-Z0-9_/-]{1,250}\z", RegexOptions.Compiled);

    private readonly string _cloudName;
    private readonly string _apiKey;
    private readonly string _apiSecret;

    public sealed record UploadedCover(string SecureUrl, string PublicId);

    public static bool IsConfigured(IConfiguration config) =>
        !string.IsNullOrWhiteSpace(config["Cloudinary:CloudName"]) &&
        !string.IsNullOrWhiteSpace(config["Cloudinary:ApiKey"]) &&
        !string.IsNullOrWhiteSpace(config["Cloudinary:ApiSecret"]);

    public CloudinaryBookCoverStorage(IConfiguration config)
    {
        if (!IsConfigured(config))
            throw new InvalidOperationException("Cloudinary chưa được cấu hình. Thiết lập CloudName, ApiKey và ApiSecret bằng User Secrets.");

        _cloudName = config["Cloudinary:CloudName"]!.Trim();
        _apiKey = config["Cloudinary:ApiKey"]!.Trim();
        _apiSecret = config["Cloudinary:ApiSecret"]!.Trim();
        if (!CloudNamePattern.IsMatch(_cloudName) || _apiKey.Length is < 3 or > 150 ||
            _apiSecret.Length is < 3 or > 512 || _apiKey.Contains(':'))
            throw new InvalidOperationException("Cấu hình Cloudinary không hợp lệ.");
    }

    private HttpRequestMessage CreateRequest(string action, HttpContent content)
    {
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://api.cloudinary.com/v1_1/{_cloudName}/image/{action}")
        { Content = content };
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes(_apiKey + ":" + _apiSecret));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        return request;
    }

    public async Task<UploadedCover> UploadAsync(string publicId, Stream image,
        string contentType, CancellationToken ct)
    {
        if (!PublicIdPattern.IsMatch(publicId) || publicId.Contains("..", StringComparison.Ordinal))
            throw new InvalidOperationException("Mã ảnh Cloudinary không hợp lệ.");
        if (contentType is not ("image/jpeg" or "image/png" or "image/webp"))
            throw new InvalidOperationException("Định dạng ảnh không được chấp nhận.");
        if (!image.CanRead || !image.CanSeek || image.Length is < 12 or > 5L * 1024 * 1024)
            throw new InvalidOperationException("Ảnh phải có dung lượng tối đa 5 MB.");

        using var form = new MultipartFormDataContent();
        using var file = new StreamContent(image);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "file", "cover" + (contentType == "image/png" ? ".png" :
            contentType == "image/webp" ? ".webp" : ".jpg"));
        form.Add(new StringContent(publicId), "public_id");
        // Unique server-generated IDs avoid overwrites/caches when updating book covers.
        using var request = CreateRequest("upload", form);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("Cloudinary từ chối tải ảnh (HTTP " + (int)response.StatusCode + "). Kiểm tra cấu hình/giới hạn tài khoản.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = json.RootElement;
        if (!root.TryGetProperty("public_id", out var idValue) ||
            !root.TryGetProperty("secure_url", out var urlValue))
            throw new InvalidOperationException("Cloudinary không trả về URL và public_id.");
        string returnedId = idValue.GetString() ?? "";
        string secureUrl = urlValue.GetString() ?? "";
        if (!string.Equals(returnedId, publicId, StringComparison.Ordinal) ||
            !Uri.TryCreate(secureUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("res.cloudinary.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.StartsWith("/" + _cloudName + "/image/upload/", StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException("Cloudinary trả về ảnh không khớp yêu cầu.");
        return new UploadedCover(secureUrl, returnedId);
    }

    // Best-effort cleanup for a newly uploaded image if the DB commit failed.
    // Only IDs within the app namespace are eligible; never delete existing covers.
    public async Task DeleteAsync(string publicId, CancellationToken ct)
    {
        if (!PublicIdPattern.IsMatch(publicId) || publicId.Contains("..", StringComparison.Ordinal))
            throw new InvalidOperationException("Mã ảnh Cloudinary không hợp lệ.");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["public_id"] = publicId,
            ["invalidate"] = "true"
        });
        using var request = CreateRequest("destroy", form);
        using var response = await Http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("Cloudinary cleanup failed: HTTP " + (int)response.StatusCode);
    }

    public void Dispose() { /* Shared HttpClient intentionally not disposed. */ }
}
