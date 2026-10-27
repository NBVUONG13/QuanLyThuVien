using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace QuanLyThuVien.Services
{
    // WPF .NET Framework 4.7.2: chi goi API, KHONG ket noi PostgreSQL truc tiep.
    public sealed class AuthApiClient
    {
        private const string LoginUrl = "http://127.0.0.1:5082/api/auth/login";
        private const string MeUrl = "http://127.0.0.1:5082/api/auth/me";
        private const string LogoutUrl = "http://127.0.0.1:5082/api/auth/logout";
        private static readonly HttpClient Http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        public async Task<AuthLoginResult> LoginAsync(
            string username,
            string password,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
                return AuthLoginResult.Failure("Vui long nhap tai khoan va mat khau.");

            var request = new LoginRequestDto
            {
                Username = username.Trim(),
                Password = password
            };

            try
            {
                string json = SerializeJson(request);
                using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
                using (var response = await Http.PostAsync(LoginUrl, content, cancellationToken)
                                               .ConfigureAwait(false))
                {
                    if (response.StatusCode == HttpStatusCode.Unauthorized)
                        return AuthLoginResult.Failure("Sai tai khoan, mat khau hoac tai khoan chua kich hoat.");

                    if (!response.IsSuccessStatusCode)
                        return AuthLoginResult.Failure(
                            "API dang gap loi (HTTP " + (int)response.StatusCode + ").");

                    string responseJson = await response.Content.ReadAsStringAsync()
                                                        .ConfigureAwait(false);
                    var result = DeserializeJson<LoginResponseDto>(responseJson);
                    if (result == null || result.Message != "credentials_valid" ||
                        result.User == null || result.User.UserId <= 0 ||
                        string.IsNullOrWhiteSpace(result.User.Role) ||
                        string.IsNullOrWhiteSpace(result.Token))
                    {
                        return AuthLoginResult.Failure("API tra ve du lieu khong hop le.");
                    }

                    return AuthLoginResult.Success(
                        result.User.UserId, result.User.Username,
                        result.User.Role, result.Token);
                }
            }
            catch (HttpRequestException)
            {
                return AuthLoginResult.Failure(
                    "Khong ket noi duoc API. Hay chay SmartLibrary.Api truoc.");
            }
            catch (TaskCanceledException)
            {
                return AuthLoginResult.Failure("API khong phan hoi kip hoac yeu cau da huy.");
            }
            catch (SerializationException)
            {
                return AuthLoginResult.Failure("Khong doc duoc phan hoi JSON tu API.");
            }
        }

        // Goi /me voi Bearer token; moi quyen xu ly tai API van phai kiem tra session.
        public async Task<AuthLoginResult> ValidateAsync(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return AuthLoginResult.Failure("Chua co phien dang nhap.");
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Get, MeUrl))
                {
                    req.Headers.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                    using (var response = await Http.SendAsync(req).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                            return AuthLoginResult.Failure("Phien da het han hoac khong hop le.");
                        string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var data = DeserializeJson<MeResponseDto>(json);
                        if (data == null || data.UserId <= 0 ||
                            string.IsNullOrWhiteSpace(data.Role))
                            return AuthLoginResult.Failure("Du lieu phien khong hop le.");
                        return AuthLoginResult.Success(data.UserId, data.Username, data.Role, token);
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException ||
                                       ex is SerializationException)
            {
                return AuthLoginResult.Failure("Khong kiem tra duoc phien dang nhap.");
            }
        }

        public async Task<bool> LogoutAsync(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return false;
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Post, LogoutUrl))
                {
                    req.Headers.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                    using (var response = await Http.SendAsync(req).ConfigureAwait(false))
                        return response.IsSuccessStatusCode;
                }
            }
            catch (HttpRequestException) { return false; }
            catch (TaskCanceledException) { return false; }
        }

        private static string SerializeJson<T>(T value)
        {
            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, value);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        private static T DeserializeJson<T>(string json)
        {
            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                return (T)serializer.ReadObject(stream);
            }
        }

        [DataContract]
        private sealed class LoginRequestDto
        {
            [DataMember(Name = "username")]
            public string Username { get; set; }

            [DataMember(Name = "password")]
            public string Password { get; set; }
        }

        [DataContract]
        private sealed class LoginResponseDto
        {
            [DataMember(Name = "message")]
            public string Message { get; set; }

            [DataMember(Name = "token")]
            public string Token { get; set; }

            [DataMember(Name = "user")]
            public UserDto User { get; set; }
        }

        [DataContract]
        private sealed class MeResponseDto
        {
            [DataMember(Name = "userId")] public long UserId { get; set; }
            [DataMember(Name = "username")] public string Username { get; set; }
            [DataMember(Name = "role")] public string Role { get; set; }
        }

        [DataContract]
        private sealed class UserDto
        {
            [DataMember(Name = "userId")]
            public long UserId { get; set; }

            [DataMember(Name = "username")]
            public string Username { get; set; }

            [DataMember(Name = "role")]
            public string Role { get; set; }
        }
    }

    public sealed class AuthLoginResult
    {
        public bool IsSuccess { get; private set; }
        public long UserId { get; private set; }
        public string Username { get; private set; }
        public string Role { get; private set; }
        public string Token { get; private set; }
        public string Message { get; private set; }

        internal static AuthLoginResult Success(long userId, string username, string role, string token)
        {
            return new AuthLoginResult
            {
                IsSuccess = true,
                UserId = userId,
                Username = username,
                Role = role,
                Token = token,
                Message = "Xac thuc tai khoan thanh cong."
            };
        }

        internal static AuthLoginResult Failure(string message)
        {
            return new AuthLoginResult { IsSuccess = false, Message = message };
        }
    }
}
