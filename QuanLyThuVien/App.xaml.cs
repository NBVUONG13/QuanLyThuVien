using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace QuanLyThuVien
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        // Single place to configure local development vs remote HTTPS API.
        // Copy App.config with EXE, or update QuanLyThuVien.exe.config after publish.
        private static readonly string ApiBaseUrlValue = LoadApiBaseUrl();
        public static string ApiBaseUrl { get { return ApiBaseUrlValue; } }

        private static string LoadApiBaseUrl()
        {
            string text = ConfigurationManager.AppSettings["SmartLibraryApiBaseUrl"];
            if (string.IsNullOrWhiteSpace(text)) text = "http://127.0.0.1:5082";
            text = text.Trim().TrimEnd('/');
            Uri uri;
            if (!Uri.TryCreate(text, UriKind.Absolute, out uri) ||
                (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                 !(string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                   uri.IsLoopback)) ||
                !string.IsNullOrEmpty(uri.UserInfo) ||
                !string.IsNullOrEmpty(uri.Query) ||
                !string.IsNullOrEmpty(uri.Fragment) ||
                uri.AbsolutePath != "/")
                throw new ConfigurationErrorsException("SmartLibraryApiBaseUrl must be an HTTPS server origin or local loopback URL.");
            return uri.GetLeftPart(UriPartial.Authority);
        }
    }
}
