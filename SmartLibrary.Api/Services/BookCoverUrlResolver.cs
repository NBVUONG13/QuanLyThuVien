using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using System.Text.RegularExpressions;

namespace SmartLibrary.Api.Services;

// Cloudinary URL is stored in books.cover_url, public_id in cover_object_key.
// Legacy local files and already-existing HTTPS image URLs keep working.
public static class BookCoverUrlResolver
{
    public const string LocalBaseUrl = "http://127.0.0.1:5082";
    private const string DefaultCover = "/covers/sachdemo.jpg";
    private static readonly Regex LocalCoverPattern = new(
        @"\A/covers/[A-Za-z0-9_-][A-Za-z0-9_.-]*\.(?:jpg|jpeg|png|webp)\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string Resolve(string? coverUrl, string? coverObjectKey,
        IConfiguration configuration, IWebHostEnvironment environment)
    {
        string value = (coverUrl ?? "").Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttps ||
             (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)) &&
            uri.UserInfo.Length == 0)
            return uri.AbsoluteUri;

        if (LocalCoverPattern.IsMatch(value) && LocalFileExists(value, environment))
            return LocalBaseUrl + value;

        // Transitional compatibility for pre-existing R2 object keys, if configured.
        string key = (coverObjectKey ?? "").Trim();
        string r2Base = (configuration["BookCovers:PublicBaseUrl"] ?? "").Trim();
        if (IsSafeObjectKey(key) && key.StartsWith("covers/", StringComparison.Ordinal) &&
            Uri.TryCreate(r2Base, UriKind.Absolute, out var cdn) &&
            cdn.Scheme == Uri.UriSchemeHttps && cdn.UserInfo.Length == 0)
            return r2Base.TrimEnd('/') + "/" +
                string.Join("/", key.Split('/').Select(Uri.EscapeDataString));

        return LocalBaseUrl + DefaultCover;
    }

    private static bool IsSafeObjectKey(string key)
    {
        if (key.Length == 0 || key.Length > 512 || key.StartsWith('/') ||
            key.Contains('\\') || key.Contains('?') || key.Contains('#')) return false;
        return key.Split('/').All(p => p.Length > 0 && p != "." && p != ".." && !p.Contains(':'));
    }

    private static bool LocalFileExists(string relative, IWebHostEnvironment environment)
    {
        string file = relative["/covers/".Length..];
        return File.Exists(Path.Combine(environment.ContentRootPath, "wwwroot", "covers", file));
    }
}
