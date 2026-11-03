using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using System.Text.RegularExpressions;

namespace SmartLibrary.Api.Services;

// Book cover URLs for the LOCAL development API. Database values stay unchanged.
// If the local file is missing, use wwwroot/covers/sachdemo.jpg.
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
        if (LocalCoverPattern.IsMatch(value) && LocalFileExists(value, environment))
            return LocalBaseUrl + value;

        // Preserve support for an existing HTTPS CDN link, if one was stored in cover_url.
        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? full) &&
            (full.Scheme == Uri.UriSchemeHttps ||
             (full.Scheme == Uri.UriSchemeHttp && full.IsLoopback)))
            return full.AbsoluteUri;

        // Keep the existing cover_object_key + BookCovers:PublicBaseUrl contract for R2.
        string key = (coverObjectKey ?? "").Trim();
        string cdnBase = (configuration["BookCovers:PublicBaseUrl"] ?? "").Trim();
        if (Uri.TryCreate(cdnBase, UriKind.Absolute, out Uri? cdn) &&
            cdn.Scheme == Uri.UriSchemeHttps && key.Length > 0 &&
            !key.StartsWith('/') && !key.Contains('\\') &&
            !key.Contains('?') && !key.Contains('#'))
        {
            string[] parts = key.Split('/');
            if (parts.All(p => p.Length > 0 && p != "." && p != ".." && !p.Contains(':')))
                return cdnBase.TrimEnd('/') + "/" +
                    string.Join("/", parts.Select(Uri.EscapeDataString));
        }

        return LocalBaseUrl + DefaultCover;
    }

    private static bool LocalFileExists(string relativePath, IWebHostEnvironment environment)
    {
        // Pattern above guarantees a single filename below covers; cannot traverse folders.
        string fileName = relativePath["/covers/".Length..];
        string path = Path.Combine(environment.ContentRootPath, "wwwroot", "covers", fileName);
        return File.Exists(path);
    }
}
