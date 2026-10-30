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


        // STEP 52: Reader self profile and authorized reader listing.
        public async Task<ReaderProfileResponse> GetMyReaderProfileAsync(string token)
        {
            return await GetReaderDataAsync("http://127.0.0.1:5082/api/readers/me", token);
        }

        public async Task<ReaderProfileResponse> FindReadersAsync(string token, string query)
        {
            string url = "http://127.0.0.1:5082/api/readers?query=" +
                         Uri.EscapeDataString((query ?? "").Trim());
            return await GetReaderDataAsync(url, token);
        }

        private static async Task<ReaderProfileResponse> GetReaderDataAsync(string url, string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return new ReaderProfileResponse { Message = "Bạn chưa đăng nhập." };
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    request.Headers.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                    using (var response = await Http.SendAsync(request).ConfigureAwait(false))
                    {
                        if (response.StatusCode == HttpStatusCode.Unauthorized)
                            return new ReaderProfileResponse { Message = "Phiên đăng nhập đã hết hạn." };
                        if (response.StatusCode == HttpStatusCode.Forbidden)
                            return new ReaderProfileResponse { Message = "Không có quyền truy cập." };
                        if (!response.IsSuccessStatusCode)
                            return new ReaderProfileResponse { Message = "Không tải được độc giả. HTTP " + (int)response.StatusCode };
                        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var data = DeserializeJson<ReaderProfileResponse>(json);
                        if (data == null)
                            return new ReaderProfileResponse { Message = "Dữ liệu phản hồi không hợp lệ." };
                        data.IsSuccess = true;
                        return data;
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException ||
                                       ex is SerializationException || ex is ArgumentException)
            {
                return new ReaderProfileResponse { Message = "Không kết nối hoặc đọc được API độc giả." };
            }
        }



        // STEP 53. Profile changes MUST use server OTP flow; no direct profile PUT/PATCH.
        public async Task<ProfileEditStartResult> StartProfileEditAsync(string token, string fullName, string phone)
        {
            if (string.IsNullOrWhiteSpace(token))
                return new ProfileEditStartResult { Message = "Phiên đăng nhập không hợp lệ." };
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Post,
                    "http://127.0.0.1:5082/api/readers/me/edit/start"))
                {
                    request.Headers.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                    request.Content = new StringContent(SerializeJson(new ProfileEditStartDto
                    { FullName = fullName, Phone = phone }), Encoding.UTF8, "application/json");
                    using (var response = await Http.SendAsync(request).ConfigureAwait(false))
                    {
                        string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (!response.IsSuccessStatusCode)
                            return new ProfileEditStartResult
                            {
                                Message = ReadApiError(json,
                                "Không gửi được yêu cầu OTP (HTTP " + (int)response.StatusCode + ").")
                            };
                        var data = DeserializeJson<ProfileEditStartResult>(json);
                        if (data == null || data.RequestId == Guid.Empty)
                            return new ProfileEditStartResult { Message = "API trả về yêu cầu OTP không hợp lệ." };
                        data.IsSuccess = true;
                        return data;
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException ||
                                       ex is SerializationException || ex is ArgumentException)
            { return new ProfileEditStartResult { Message = "Không kết nối được API xác nhận OTP." }; }
        }

        public async Task<ProfileEditConfirmResult> ConfirmProfileEditAsync(string token, Guid requestId, string otp)
        {
            if (string.IsNullOrWhiteSpace(token))
                return new ProfileEditConfirmResult { Message = "Phiên đăng nhập không hợp lệ." };
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Post,
                    "http://127.0.0.1:5082/api/readers/me/edit/confirm"))
                {
                    request.Headers.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                    request.Content = new StringContent(SerializeJson(new ProfileEditConfirmDto
                    { RequestId = requestId, Otp = otp }), Encoding.UTF8, "application/json");
                    using (var response = await Http.SendAsync(request).ConfigureAwait(false))
                    {
                        string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (!response.IsSuccessStatusCode)
                            return new ProfileEditConfirmResult
                            {
                                Message = ReadApiError(json,
                                "Không thể xác nhận OTP (HTTP " + (int)response.StatusCode + ").")
                            };
                        var data = DeserializeJson<ProfileEditConfirmResult>(json);
                        if (data == null || data.Message != "profile_updated")
                            return new ProfileEditConfirmResult { Message = "API không xác nhận cập nhật hồ sơ." };
                        return new ProfileEditConfirmResult { IsSuccess = true, Message = "Cập nhật hồ sơ thành công." };
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException ||
                                       ex is SerializationException || ex is ArgumentException)
            { return new ProfileEditConfirmResult { Message = "Không kết nối được API xác nhận OTP." }; }
        }


        // STEP 65: Staff-assisted, single-use code for an EXISTING Guest profile.
        // The code is never written to local files or logs.
        public async Task<GuestClaimIssueResult> IssueGuestClaimAsync(string token, long readerId)
        {
            if (readerId <= 0 || string.IsNullOrWhiteSpace(token))
                return new GuestClaimIssueResult { Message = "Chưa có Guest hoặc phiên hợp lệ." };
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Post,
                    "http://127.0.0.1:5082/api/readers/" + readerId + "/guest-claim"))
                {
                    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                    req.Content = new StringContent("{\"staffVerifiedIdentity\":true}", Encoding.UTF8, "application/json");
                    using (var res = await Http.SendAsync(req))
                    {
                        string json = await res.Content.ReadAsStringAsync();
                        if (!res.IsSuccessStatusCode)
                            return new GuestClaimIssueResult
                            {
                                Message = ReadApiError(json,
                                "Không cấp được mã liên kết (HTTP " + (int)res.StatusCode + ").")
                            };
                        var result = DeserializeJson<GuestClaimIssueResult>(json);
                        if (result == null || string.IsNullOrWhiteSpace(result.GuestClaimCode))
                            return new GuestClaimIssueResult { Message = "Phản hồi API không hợp lệ." };
                        result.IsSuccess = true;
                        return result;
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException ||
                                        ex is SerializationException || ex is ArgumentException)
            { return new GuestClaimIssueResult { Message = "Không kết nối được API liên kết Guest." }; }
        }

        // STEP 57: Reader insights and borrow-access decisions for Staff/Admin.
        public async Task<ReaderInsightResponse> GetReaderInsightsAsync(string token, long readerId)
        {
            if (readerId <= 0 || string.IsNullOrWhiteSpace(token))
                return new ReaderInsightResponse { Message = "Phiên/ID độc giả không hợp lệ." };
            try
            {
                var url = "http://127.0.0.1:5082/api/readers/" + readerId + "/insights";
                using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                    using (var res = await Http.SendAsync(req))
                    {
                        var json = await res.Content.ReadAsStringAsync();
                        if (!res.IsSuccessStatusCode)
                            return new ReaderInsightResponse
                            {
                                Message = ReadApiError(json,
                                "Không xem được chi tiết độc giả (HTTP " + (int)res.StatusCode + ").")
                            };
                        var data = DeserializeJson<ReaderInsightResponse>(json);
                        if (data == null || data.Reader == null || data.Statistics == null)
                            return new ReaderInsightResponse { Message = "API trả về chi tiết không hợp lệ." };
                        data.IsSuccess = true;
                        return data;
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException ||
                                      ex is SerializationException || ex is ArgumentException)
            { return new ReaderInsightResponse { Message = "Không tải được chi tiết độc giả." }; }
        }
        public async Task<ReaderBorrowAccessResult> ChangeReaderBorrowAccessAsync(
            string token, long readerId, bool block, string reason)
        {
            if (readerId <= 0 || string.IsNullOrWhiteSpace(token))
                return new ReaderBorrowAccessResult { Message = "Phiên/ID độc giả không hợp lệ." };
            try
            {
                string url = "http://127.0.0.1:5082/api/readers/" + readerId + "/borrow-access";
                using (var req = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                    req.Content = new StringContent(SerializeJson(new BorrowAccessEditDto
                    { Block = block, Reason = reason }), Encoding.UTF8, "application/json");
                    using (var res = await Http.SendAsync(req))
                    {
                        var json = await res.Content.ReadAsStringAsync();
                        if (!res.IsSuccessStatusCode)
                            return new ReaderBorrowAccessResult
                            {
                                Message = ReadApiError(json,
                              "Không thay đổi được quyền mượn (HTTP " + (int)res.StatusCode + ").")
                            };
                        var data = DeserializeJson<ReaderBorrowAccessResult>(json);
                        return new ReaderBorrowAccessResult
                        {
                            IsSuccess = true,
                            Message = data?.Message ?? "Cập nhật quyền mượn thành công."
                        };
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException ||
                                      ex is SerializationException || ex is ArgumentException)
            { return new ReaderBorrowAccessResult { Message = "Không kết nối được API quyền mượn." }; }
        }
        [DataContract]
        private sealed class BorrowAccessEditDto
        {
            [DataMember(Name = "block")] public bool Block { get; set; }
            [DataMember(Name = "reason")] public string Reason { get; set; }
        }

        private static string ReadApiError(string json, string fallback)
        {
            try
            {
                var error = DeserializeJson<ProfileEditConfirmResult>(json);
                if (!string.IsNullOrWhiteSpace(error?.Message)) return error.Message;
            }
            catch (Exception) { }
            return fallback;
        }

        [DataContract]
        private sealed class ProfileEditStartDto
        {
            [DataMember(Name = "fullName")] public string FullName { get; set; }
            [DataMember(Name = "phone")] public string Phone { get; set; }
        }
        [DataContract]
        private sealed class ProfileEditConfirmDto
        {
            [DataMember(Name = "requestId")] public Guid RequestId { get; set; }
            [DataMember(Name = "otp")] public string Otp { get; set; }
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


    [DataContract]
    public sealed class ReaderProfileResponse
    {
        [IgnoreDataMember] public bool IsSuccess { get; set; }
        [DataMember(Name = "message")] public string Message { get; set; }
        [DataMember(Name = "reader")] public ReaderProfileItem Reader { get; set; }
        [DataMember(Name = "readers")] public System.Collections.Generic.List<ReaderProfileItem> Readers { get; set; }
    }

    [DataContract]
    public sealed class ReaderProfileItem
    {
        [DataMember(Name = "userId")] public long UserId { get; set; }
        [DataMember(Name = "username")] public string Username { get; set; }
        [DataMember(Name = "readerCode")] public string ReaderCode { get; set; }
        [DataMember(Name = "fullName")] public string FullName { get; set; }
        [DataMember(Name = "tierName")] public string TierName { get; set; }
        [DataMember(Name = "status")] public string Status { get; set; }
        [DataMember(Name = "isGuest")] public bool IsGuest { get; set; }
        // Only returned by GET /api/readers/me; NEVER included in admin/staff list.
        [DataMember(Name = "phone")] public string Phone { get; set; }
        [DataMember(Name = "phoneVerified")] public bool PhoneVerified { get; set; }
        [DataMember(Name = "maxActiveCopies")] public int MaxActiveCopies { get; set; }
        [DataMember(Name = "maxLoanDays")] public int MaxLoanDays { get; set; }
    }


    [DataContract]
    public sealed class ProfileEditStartResult
    {
        [IgnoreDataMember] public bool IsSuccess { get; set; }
        [DataMember(Name = "message")] public string Message { get; set; }
        [DataMember(Name = "requestId")] public Guid RequestId { get; set; }
    }

    [DataContract]
    public sealed class ProfileEditConfirmResult
    {
        [IgnoreDataMember] public bool IsSuccess { get; set; }
        [DataMember(Name = "message")] public string Message { get; set; }
    }


    [DataContract]
    public sealed class GuestClaimIssueResult
    {
        [IgnoreDataMember] public bool IsSuccess { get; set; }
        [DataMember(Name = "message")] public string Message { get; set; }
        [DataMember(Name = "guestClaimCode")] public string GuestClaimCode { get; set; }
        [DataMember(Name = "expiresAt")] public string ExpiresAt { get; set; }
    }

    // STEP 57: API DTOs; live binding is done in ReaderManagementViewModel.
    [DataContract]
    public sealed class ReaderInsightResponse
    {
        [IgnoreDataMember] public bool IsSuccess { get; set; }
        [DataMember(Name = "message")] public string Message { get; set; }
        [DataMember(Name = "reader")] public ReaderInsightHeader Reader { get; set; }
        [DataMember(Name = "statistics")] public ReaderInsightStats Statistics { get; set; }
        [DataMember(Name = "loans")] public System.Collections.Generic.List<ReaderLoanHistory> Loans { get; set; }
        [DataMember(Name = "restrictions")] public System.Collections.Generic.List<ReaderAccessEvent> Restrictions { get; set; }
        [DataMember(Name = "monthlyBorrowCounts")] public System.Collections.Generic.List<ReaderMonthStat> MonthlyBorrowCounts { get; set; }
    }
    [DataContract]
    public sealed class ReaderInsightHeader
    {
        [DataMember(Name = "userId")] public long UserId { get; set; }
        [DataMember(Name = "username")] public string Username { get; set; }
        [DataMember(Name = "fullName")] public string FullName { get; set; }
        [DataMember(Name = "readerCode")] public string ReaderCode { get; set; }
        [DataMember(Name = "tierName")] public string TierName { get; set; }
        [DataMember(Name = "accountStatus")] public string AccountStatus { get; set; }
        [DataMember(Name = "phone")] public string Phone { get; set; }
        [DataMember(Name = "maxActiveCopies")] public int MaxActiveCopies { get; set; }
        [DataMember(Name = "maxLoanDays")] public int MaxLoanDays { get; set; }
        [DataMember(Name = "borrowBlocked")] public bool BorrowBlocked { get; set; }
        public string BorrowAccessText => BorrowBlocked ? "ĐANG BỊ CẤM MƯỢN" : "Được phép mượn";
    }
    [DataContract]
    public sealed class ReaderInsightStats
    {
        [DataMember(Name = "totalLoans")] public long TotalLoans { get; set; }
        [DataMember(Name = "totalCopies")] public long TotalCopies { get; set; }
        [DataMember(Name = "returnedCopies")] public long ReturnedCopies { get; set; }
        [DataMember(Name = "activeCopies")] public long ActiveCopies { get; set; }
        [DataMember(Name = "overdueCount")] public long OverdueCount { get; set; }
        [DataMember(Name = "totalFine")] public decimal TotalFine { get; set; }
        [DataMember(Name = "outstandingFine")] public decimal OutstandingFine { get; set; }
    }
    [DataContract]
    public sealed class ReaderLoanHistory
    {
        [DataMember(Name = "title")] public string Title { get; set; }
        [DataMember(Name = "copyCode")] public string CopyCode { get; set; }
        [DataMember(Name = "borrowedAt")] public string BorrowedAt { get; set; }
        [DataMember(Name = "dueAt")] public string DueAt { get; set; }
        [DataMember(Name = "returnedAt")] public string ReturnedAt { get; set; }
        [DataMember(Name = "status")] public string Status { get; set; }
        [DataMember(Name = "isOverdue")] public bool IsOverdue { get; set; }
        public string LateText => IsOverdue ? "Có" : "Không";
    }
    [DataContract]
    public sealed class ReaderAccessEvent
    {
        [DataMember(Name = "action")] public string Action { get; set; }
        [DataMember(Name = "reason")] public string Reason { get; set; }
        [DataMember(Name = "at")] public string At { get; set; }
        [DataMember(Name = "by")] public string By { get; set; }
    }
    [DataContract]
    public sealed class ReaderMonthStat
    {
        [DataMember(Name = "month")] public string Month { get; set; }
        [DataMember(Name = "count")] public long Count { get; set; }
        [IgnoreDataMember] public double Percent { get; set; }
    }
    [DataContract]
    public sealed class ReaderBorrowAccessResult
    {
        [IgnoreDataMember] public bool IsSuccess { get; set; }
        [DataMember(Name = "message")] public string Message { get; set; }
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
