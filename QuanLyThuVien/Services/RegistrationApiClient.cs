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
    // Client WPF .NET Framework 4.7.2. API dang ky chi duoc bat o Development.
    // OTP duoc in tai console API; day KHONG phai xac minh so dien thoai qua SMS that.
    public sealed class RegistrationApiClient
    {
        private const string StartUrl = "http://127.0.0.1:5082/api/auth/register/start";
        private const string ConfirmUrl = "http://127.0.0.1:5082/api/auth/register/confirm";
        private const string PreviewUrl = "http://127.0.0.1:5082/api/auth/register/guest-preview";

        private static readonly HttpClient Http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        public async Task<GuestPreviewResult> PreviewGuestAsync(
            string guestClaimCode,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(guestClaimCode) ||
                !System.Text.RegularExpressions.Regex.IsMatch(guestClaimCode.Trim(), @"\A[0-9]{8}\z"))
                return GuestPreviewResult.Failure("Nhập đúng mã liên kết 8 số do Staff cấp.");
            try
            {
                var data = new GuestPreviewRequest
                {
                    GuestClaimCode = guestClaimCode.Trim()
                };
                using (var response = await SendAsync(PreviewUrl, data, cancellationToken)
                    .ConfigureAwait(false))
                {
                    string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                        return GuestPreviewResult.Failure(GetErrorMessage(response, body));
                    var result = Deserialize<GuestPreviewResponse>(body);
                    if (result == null || result.Message != "guest_preview_ready_dev_only" ||
                        string.IsNullOrWhiteSpace(result.FullName))
                        return GuestPreviewResult.Failure("API trả về hồ sơ Guest không hợp lệ.");
                    return GuestPreviewResult.Success(result.FullName, result.Phone ?? "");
                }
            }
            catch (HttpRequestException) { return GuestPreviewResult.Failure("Không kết nối được API."); }
            catch (TaskCanceledException) { return GuestPreviewResult.Failure("Hết thời gian tải hồ sơ."); }
            catch (SerializationException) { return GuestPreviewResult.Failure("JSON hồ sơ Guest không hợp lệ."); }
            catch (FormatException) { return GuestPreviewResult.Failure("Dữ liệu hồ sơ Guest không hợp lệ."); }
        }

        public async Task<RegistrationStartResult> StartAsync(
            string fullName, string username, string phone, string password,
            string guestClaimCode,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(fullName) ||
                string.IsNullOrWhiteSpace(username) ||
                string.IsNullOrWhiteSpace(phone) ||
                string.IsNullOrWhiteSpace(password))
            {
                return RegistrationStartResult.Failure("Vui lòng nhập đầy đủ thông tin đăng ký.");
            }

            var data = new StartRequest
            {
                FullName = fullName.Trim(),
                Username = username.Trim(),
                Phone = phone.Trim(),
                Password = password,
                GuestClaimCode = guestClaimCode == null ? "" : guestClaimCode.Trim()
            };

            try
            {
                using (var response = await SendAsync(StartUrl, data, cancellationToken)
                    .ConfigureAwait(false))
                {
                    string body = await response.Content.ReadAsStringAsync()
                        .ConfigureAwait(false);

                    if (!response.IsSuccessStatusCode)
                        return RegistrationStartResult.Failure(GetErrorMessage(response, body));

                    var result = Deserialize<StartResponse>(body);
                    if (result == null || result.Message != "dev_otp_created" ||
                        result.RequestId == Guid.Empty)
                    {
                        return RegistrationStartResult.Failure(
                            "API trả về yêu cầu đăng ký không hợp lệ.");
                    }

                    return RegistrationStartResult.Success(
                        result.RequestId,
                        result.ExpiresAt,
                        "Đã tạo yêu cầu. Xem mã OTP trong cửa sổ API (chỉ thử nghiệm).");
                }
            }
            catch (HttpRequestException)
            {
                return RegistrationStartResult.Failure(
                    "Không kết nối được API. Hãy chạy SmartLibrary.Api trước.");
            }
            catch (TaskCanceledException)
            {
                return RegistrationStartResult.Failure("Yêu cầu hết thời gian hoặc đã hủy.");
            }
            catch (SerializationException)
            {
                return RegistrationStartResult.Failure("Không đọc được dữ liệu JSON từ API.");
            }
            catch (FormatException)
            {
                return RegistrationStartResult.Failure("API trả về dữ liệu không đúng định dạng.");
            }
        }

        public async Task<RegistrationConfirmResult> ConfirmAsync(
            Guid requestId, string otp,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (requestId == Guid.Empty || string.IsNullOrWhiteSpace(otp) ||
                otp.Length != 6)
                return RegistrationConfirmResult.Failure("Hãy nhập đúng mã OTP gồm 6 chữ số.");

            try
            {
                var data = new ConfirmRequest { RequestId = requestId, Otp = otp.Trim() };
                using (var response = await SendAsync(ConfirmUrl, data, cancellationToken)
                    .ConfigureAwait(false))
                {
                    string body = await response.Content.ReadAsStringAsync()
                        .ConfigureAwait(false);

                    if (!response.IsSuccessStatusCode)
                        return RegistrationConfirmResult.Failure(GetErrorMessage(response, body));

                    var result = Deserialize<ConfirmResponse>(body);
                    if (result == null || result.Message != "registration_completed_dev_only" ||
                        result.UserId <= 0 || result.Role != "Reader")
                        return RegistrationConfirmResult.Failure(
                            "API trả về kết quả đăng ký không hợp lệ.");

                    return RegistrationConfirmResult.Success(
                        result.UserId, result.Username, result.Role);
                }
            }
            catch (HttpRequestException)
            {
                return RegistrationConfirmResult.Failure(
                    "Không kết nối được API. Hãy chạy SmartLibrary.Api trước.");
            }
            catch (TaskCanceledException)
            {
                return RegistrationConfirmResult.Failure("Yêu cầu hết thời gian hoặc đã hủy.");
            }
            catch (SerializationException)
            {
                return RegistrationConfirmResult.Failure("Không đọc được dữ liệu JSON từ API.");
            }
            catch (FormatException)
            {
                return RegistrationConfirmResult.Failure("API trả về dữ liệu không đúng định dạng.");
            }
        }

        private static async Task<HttpResponseMessage> SendAsync<T>(
            string url, T data, CancellationToken cancellationToken)
        {
            string json = Serialize(data);
            using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
            {
                return await Http.PostAsync(url, content, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        private static string GetErrorMessage(HttpResponseMessage response, string body)
        {
            try
            {
                var error = Deserialize<ErrorResponse>(body);
                if (error != null && !string.IsNullOrWhiteSpace(error.Message))
                    return error.Message;
            }
            catch (SerializationException) { }
            catch (FormatException) { }

            if (response.StatusCode == HttpStatusCode.NotFound)
                return "API đăng ký chưa được bật. Hãy chạy API ở môi trường Development.";
            return "API từ chối yêu cầu (HTTP " + (int)response.StatusCode + ").";
        }

        private static string Serialize<T>(T value)
        {
            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, value);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        private static T Deserialize<T>(string json)
        {
            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                return (T)serializer.ReadObject(stream);
            }
        }

        [DataContract]
        private sealed class GuestPreviewRequest
        {
            [DataMember(Name = "guestClaimCode")] public string GuestClaimCode { get; set; }
        }

        [DataContract]
        private sealed class GuestPreviewResponse
        {
            [DataMember(Name = "message")] public string Message { get; set; }
            [DataMember(Name = "fullName")] public string FullName { get; set; }
            [DataMember(Name = "phone")] public string Phone { get; set; }
        }

        [DataContract]
        private sealed class StartRequest
        {
            [DataMember(Name = "fullName")] public string FullName { get; set; }
            [DataMember(Name = "username")] public string Username { get; set; }
            [DataMember(Name = "phone")] public string Phone { get; set; }
            [DataMember(Name = "password")] public string Password { get; set; }
            [DataMember(Name = "guestClaimCode")] public string GuestClaimCode { get; set; }
        }

        [DataContract]
        private sealed class ConfirmRequest
        {
            [DataMember(Name = "requestId")] public Guid RequestId { get; set; }
            [DataMember(Name = "otp")] public string Otp { get; set; }
        }

        [DataContract]
        private sealed class StartResponse
        {
            [DataMember(Name = "message")] public string Message { get; set; }
            [DataMember(Name = "requestId")] public Guid RequestId { get; set; }
            [DataMember(Name = "expiresAt")] public string ExpiresAt { get; set; }
        }

        [DataContract]
        private sealed class ConfirmResponse
        {
            [DataMember(Name = "message")] public string Message { get; set; }
            [DataMember(Name = "userId")] public long UserId { get; set; }
            [DataMember(Name = "username")] public string Username { get; set; }
            [DataMember(Name = "role")] public string Role { get; set; }
        }

        [DataContract]
        private sealed class ErrorResponse
        {
            [DataMember(Name = "code")] public string Code { get; set; }
            [DataMember(Name = "message")] public string Message { get; set; }
        }
    }

    public sealed class RegistrationStartResult
    {
        public bool IsSuccess { get; private set; }
        public Guid RequestId { get; private set; }
        public string ExpiresAt { get; private set; }
        public string Message { get; private set; }

        internal static RegistrationStartResult Success(Guid requestId, string expiresAt, string message)
        {
            return new RegistrationStartResult
            {
                IsSuccess = true,
                RequestId = requestId,
                ExpiresAt = expiresAt,
                Message = message
            };
        }

        internal static RegistrationStartResult Failure(string message)
        {
            return new RegistrationStartResult { IsSuccess = false, Message = message };
        }
    }

    public sealed class RegistrationConfirmResult
    {
        public bool IsSuccess { get; private set; }
        public long UserId { get; private set; }
        public string Username { get; private set; }
        public string Role { get; private set; }
        public string Message { get; private set; }

        internal static RegistrationConfirmResult Success(long userId, string username, string role)
        {
            return new RegistrationConfirmResult
            {
                IsSuccess = true,
                UserId = userId,
                Username = username,
                Role = role,
                Message = "Đăng ký Reader hoàn tất (chỉ thử nghiệm OTP local)."
            };
        }

        internal static RegistrationConfirmResult Failure(string message)
        {
            return new RegistrationConfirmResult { IsSuccess = false, Message = message };
        }
    }
}

namespace QuanLyThuVien.Services
{
    public sealed class GuestPreviewResult
    {
        public bool IsSuccess { get; private set; }
        public string FullName { get; private set; }
        public string Phone { get; private set; }
        public string Message { get; private set; }

        internal static GuestPreviewResult Success(string fullName, string phone) =>
            new GuestPreviewResult { IsSuccess = true, FullName = fullName, Phone = phone };
        internal static GuestPreviewResult Failure(string message) =>
            new GuestPreviewResult { IsSuccess = false, Message = message };
    }
}
