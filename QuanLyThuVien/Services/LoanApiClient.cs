using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;

namespace QuanLyThuVien.Services
{
    // WPF .NET Framework 4.7.2: all data goes through server-authorized API.
    public sealed class LoanApiClient
    {
        private const string BaseUrl = "http://127.0.0.1:5082/api/loans";
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        public async Task<LoanListResult> GetLoansAsync(string token)
        {
            try
            {
                using (var request = NewRequest(HttpMethod.Get, BaseUrl, token))
                using (var response = await Http.SendAsync(request))
                {
                    string body = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                        return new LoanListResult { Message = Error(body, response.StatusCode) };
                    var result = Parse<LoanListResponse>(body);
                    return new LoanListResult
                    {
                        IsSuccess = true,
                        Loans = result == null || result.Loans == null ? new List<LoanItem>() : result.Loans
                    };
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException ||
                                       ex is SerializationException || ex is InvalidOperationException)
            { return new LoanListResult { Message = "Không thể tải phiếu mượn. Kiểm tra API." }; }
        }

        public async Task<LoanCopyListResult> GetAvailableCopiesAsync(string token, string query)
        {
            string url = BaseUrl + "/available-copies?query=" + Uri.EscapeDataString(query ?? "");
            try
            {
                using (var request = NewRequest(HttpMethod.Get, url, token))
                using (var response = await Http.SendAsync(request))
                {
                    string body = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                        return new LoanCopyListResult { Message = Error(body, response.StatusCode) };
                    var result = Parse<LoanCopyListResponse>(body);
                    return new LoanCopyListResult
                    {
                        IsSuccess = true,
                        Copies = result == null || result.Copies == null ? new List<AvailableLoanCopy>() : result.Copies
                    };
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException ||
                                       ex is SerializationException || ex is InvalidOperationException)
            { return new LoanCopyListResult { Message = "Không thể tải danh sách cuốn còn sẵn." }; }
        }

        public Task<LoanActionResult> CheckoutAsync(string token, string readerUsername, string copyCode, int loanDays)
        {
            return SendActionAsync(BaseUrl + "/checkout", token, Serialize(new CheckoutRequest
            {
                ReaderUsername = readerUsername, CopyCode = copyCode, LoanDays = loanDays
            }));
        }

        public Task<LoanActionResult> ReturnAsync(string token, long borrowDetailId)
        {
            return SendActionAsync(BaseUrl + "/" + borrowDetailId + "/return", token, "{}");
        }

        private static async Task<LoanActionResult> SendActionAsync(string url, string token, string body)
        {
            try
            {
                using (var request = NewRequest(HttpMethod.Post, url, token))
                {
                    request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    using (var response = await Http.SendAsync(request))
                    {
                        string json = await response.Content.ReadAsStringAsync();
                        if (!response.IsSuccessStatusCode)
                            return new LoanActionResult { Message = Error(json, response.StatusCode) };
                        var payload = Parse<MessageResponse>(json);
                        return new LoanActionResult
                        {
                            IsSuccess = true,
                            Message = payload != null && !string.IsNullOrWhiteSpace(payload.Message)
                                ? payload.Message : "Thao tác thành công."
                        };
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException ||
                                       ex is SerializationException || ex is InvalidOperationException)
            { return new LoanActionResult { Message = "API không phản hồi. Vui lòng thử lại." }; }
        }

        private static HttpRequestMessage NewRequest(HttpMethod method, string url, string token)
        {
            var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return request;
        }
        private static T Parse<T>(string json)
        {
            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                return (T)serializer.ReadObject(stream);
        }
        private static string Serialize<T>(T input)
        {
            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, input);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }
        private static string Error(string body, HttpStatusCode status)
        {
            try
            {
                var result = Parse<MessageResponse>(body);
                if (result != null && !string.IsNullOrWhiteSpace(result.Message)) return result.Message;
            }
            catch (Exception) { /* Non-JSON errors are reduced to a safe HTTP status. */ }
            return status == HttpStatusCode.Unauthorized ? "Phiên hết hạn, vui lòng đăng nhập lại."
                : status == HttpStatusCode.Forbidden ? "Tài khoản không có quyền thực hiện."
                : "Yêu cầu thất bại, HTTP " + (int)status + ".";
        }

        [DataContract]
        private sealed class LoanListResponse
        {
            [DataMember(Name = "loans")] public List<LoanItem> Loans { get; set; }
        }
        [DataContract]
        private sealed class LoanCopyListResponse
        {
            [DataMember(Name = "copies")] public List<AvailableLoanCopy> Copies { get; set; }
        }
        [DataContract]
        private sealed class CheckoutRequest
        {
            [DataMember(Name = "readerUsername")] public string ReaderUsername { get; set; }
            [DataMember(Name = "copyCode")] public string CopyCode { get; set; }
            [DataMember(Name = "loanDays")] public int LoanDays { get; set; }
        }
        [DataContract]
        private sealed class MessageResponse
        {
            [DataMember(Name = "message")] public string Message { get; set; }
        }
    }

    [DataContract]
    public sealed class LoanItem
    {
        [DataMember(Name = "borrowDetailId")] public long BorrowDetailId { get; set; }
        [DataMember(Name = "borrowId")] public long BorrowId { get; set; }
        [DataMember(Name = "readerUsername")] public string ReaderUsername { get; set; }
        [DataMember(Name = "readerName")] public string ReaderName { get; set; }
        [DataMember(Name = "copyCode")] public string CopyCode { get; set; }
        [DataMember(Name = "bookTitle")] public string BookTitle { get; set; }
        [DataMember(Name = "borrowedAt")] public string BorrowedAt { get; set; }
        [DataMember(Name = "dueAt")] public string DueAt { get; set; }
        [DataMember(Name = "returnedAt")] public string ReturnedAt { get; set; }
        [DataMember(Name = "isOpen")] public bool IsOpen { get; set; }
        [DataMember(Name = "status")] public string Status { get; set; }
    }
    [DataContract]
    public sealed class AvailableLoanCopy
    {
        [DataMember(Name = "copyId")] public long CopyId { get; set; }
        [DataMember(Name = "copyCode")] public string CopyCode { get; set; }
        [DataMember(Name = "bookTitle")] public string BookTitle { get; set; }
        [DataMember(Name = "shelf")] public string Shelf { get; set; }
    }
    public sealed class LoanListResult
    {
        public bool IsSuccess { get; set; }
        public string Message { get; set; }
        public List<LoanItem> Loans { get; set; } = new List<LoanItem>();
    }
    public sealed class LoanCopyListResult
    {
        public bool IsSuccess { get; set; }
        public string Message { get; set; }
        public List<AvailableLoanCopy> Copies { get; set; } = new List<AvailableLoanCopy>();
    }
    public sealed class LoanActionResult
    {
        public bool IsSuccess { get; set; }
        public string Message { get; set; }
    }
}
