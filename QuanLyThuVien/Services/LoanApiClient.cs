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

        // Autocomplete Reader: returns only username, name and library reader code.
        public async Task<LoanReaderListResult> GetReaderSuggestionsAsync(string token, string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return new LoanReaderListResult { IsSuccess = true };
            string url = BaseUrl + "/reader-suggestions?query=" + Uri.EscapeDataString(query.Trim());
            try
            {
                using (var request = NewRequest(HttpMethod.Get, url, token))
                using (var response = await Http.SendAsync(request))
                {
                    string body = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                        return new LoanReaderListResult { Message = Error(body, response.StatusCode) };
                    var result = Parse<LoanReaderListResponse>(body);
                    return new LoanReaderListResult
                    {
                        IsSuccess = true,
                        Readers = result == null || result.Readers == null
                            ? new List<LoanReaderSuggestion>() : result.Readers
                    };
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException ||
                                       ex is SerializationException || ex is InvalidOperationException)
            { return new LoanReaderListResult { Message = "Không thể tải gợi ý độc giả." }; }
        }

        // Catalogue suggestions include titles with zero copies; no DB credentials in WPF.
        public async Task<LoanBookListResult> GetBookSuggestionsAsync(string token, string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return new LoanBookListResult { IsSuccess = true };
            string url = BaseUrl + "/book-suggestions?query=" + Uri.EscapeDataString(query.Trim());
            try
            {
                using (var request = NewRequest(HttpMethod.Get, url, token))
                using (var response = await Http.SendAsync(request))
                {
                    string body = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                        return new LoanBookListResult { Message = Error(body, response.StatusCode) };
                    var result = Parse<LoanBookListResponse>(body);
                    return new LoanBookListResult
                    {
                        IsSuccess = true,
                        Books = result == null || result.Books == null
                            ? new List<LoanBookSuggestion>() : result.Books
                    };
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException ||
                                       ex is SerializationException || ex is InvalidOperationException)
            { return new LoanBookListResult { Message = "Không tải được gợi ý đầu sách." }; }
        }

        public Task<LoanActionResult> CheckoutAsync(string token, string readerUsername, string copyCode, int loanDays)
        {
            return SendActionAsync(BaseUrl + "/checkout", token, Serialize(new CheckoutRequest
            {
                ReaderUsername = readerUsername,
                CopyCode = copyCode,
                LoanDays = loanDays
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


        // Step58: requestable book titles (includes titles with zero available physical copies).
        public async Task<LoanBookListResult> GetRequestBookSuggestionsAsync(string token, string query)
        {
            string url = "http://127.0.0.1:5082/api/borrow-requests/books?query=" +
                         Uri.EscapeDataString((query ?? "").Trim());
            try
            {
                using (var req = NewRequest(HttpMethod.Get, url, token))
                using (var response = await Http.SendAsync(req))
                {
                    var json = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                        return new LoanBookListResult { Message = Error(json, response.StatusCode) };
                    var body = Parse<LoanBookListResponse>(json);
                    return new LoanBookListResult
                    {
                        IsSuccess = true,
                        Books = body?.Books ?? new List<LoanBookSuggestion>()
                    };
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException ||
                                       ex is SerializationException || ex is InvalidOperationException)
            { return new LoanBookListResult { Message = "Không tải được gợi ý sách." }; }
        }

        public async Task<BorrowRequestListResult> GetBorrowRequestsAsync(string token)
        {
            try
            {
                using (var req = NewRequest(HttpMethod.Get,
                           "http://127.0.0.1:5082/api/borrow-requests", token))
                using (var response = await Http.SendAsync(req))
                {
                    var json = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                        return new BorrowRequestListResult { Message = Error(json, response.StatusCode) };
                    var body = Parse<BorrowRequestListResponse>(json);
                    return new BorrowRequestListResult
                    {
                        IsSuccess = true,
                        Requests = body?.Requests ?? new List<BorrowRequestSummary>()
                    };
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException ||
                                       ex is SerializationException || ex is InvalidOperationException)
            { return new BorrowRequestListResult { Message = "Không tải được đơn mượn." }; }
        }

        public Task<LoanActionResult> SubmitBorrowRequestAsync(string token,
            List<BorrowRequestLine> items, string note)
        {
            return SendActionAsync("http://127.0.0.1:5082/api/borrow-requests", token,
                Serialize(new NewBorrowRequestDto { Items = items, Note = note }));
        }

        public Task<LoanActionResult> CancelBorrowRequestAsync(string token, long requestId)
        {
            return SendActionAsync("http://127.0.0.1:5082/api/borrow-requests/" +
                                   requestId + "/cancel", token, "{}");
        }

        public Task<LoanActionResult> ReviewBorrowRequestAsync(string token, long requestId, bool approve, string note)
        {
            return SendActionAsync("http://127.0.0.1:5082/api/borrow-requests/" + requestId + "/review",
                token, Serialize(new ReviewRequestDto { Approve = approve, Note = note }));
        }

        [DataContract]
        private sealed class ReviewRequestDto
        {
            [DataMember(Name = "approve")] public bool Approve { get; set; }
            [DataMember(Name = "note")] public string Note { get; set; }
        }
        // Same borrow_requests table as Reader app orders; creates Guest and order atomically.
        public Task<LoanActionResult> CreateWalkInRequestAsync(string token, long? existingReaderId,
            string fullName, string phone, string citizenId, bool staffVerified,
            List<BorrowRequestLine> items, string note)
        {
            return SendActionAsync("http://127.0.0.1:5082/api/borrow-requests/walkin", token,
                Serialize(new WalkInRequestDto
                {
                    ExistingReaderUserId = existingReaderId,
                    FullName = fullName,
                    Phone = phone,
                    CitizenId = citizenId,
                    StaffVerifiedIdentity = staffVerified,
                    Items = items,
                    Note = note
                }));
        }
        [DataContract]
        private sealed class WalkInRequestDto
        {
            [DataMember(Name = "existingReaderUserId")] public long? ExistingReaderUserId { get; set; }
            [DataMember(Name = "fullName")] public string FullName { get; set; }
            [DataMember(Name = "phone")] public string Phone { get; set; }
            [DataMember(Name = "citizenId")] public string CitizenId { get; set; }
            [DataMember(Name = "staffVerifiedIdentity")] public bool StaffVerifiedIdentity { get; set; }
            [DataMember(Name = "items")] public List<BorrowRequestLine> Items { get; set; }
            [DataMember(Name = "note")] public string Note { get; set; }
        }

        // POST instead of GET: raw phone/CCCD is never placed in a URL/query string.
        public async Task<IdentityCheckResult> CheckReaderIdentityAsync(string token, string phone, string citizenId)
        {
            try
            {
                using (var req = NewRequest(HttpMethod.Post,
                    "http://127.0.0.1:5082/api/readers/identity-check", token))
                {
                    req.Content = new StringContent(Serialize(new IdentityCheckRequestDto
                    { Phone = phone, CitizenId = citizenId }), Encoding.UTF8, "application/json");
                    using (var response = await Http.SendAsync(req))
                    {
                        string body = await response.Content.ReadAsStringAsync();
                        if (!response.IsSuccessStatusCode)
                            return new IdentityCheckResult { Message = Error(body, response.StatusCode) };
                        var parsed = Parse<IdentityCheckResult>(body);
                        if (parsed != null) parsed.IsSuccess = true;
                        return parsed ?? new IdentityCheckResult { Message = "Phản hồi tra cứu không hợp lệ." };
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException ||
                                      ex is SerializationException || ex is InvalidOperationException)
            { return new IdentityCheckResult { Message = "Không tra cứu được hồ sơ. Kiểm tra API." }; }
        }
        [DataContract]
        private sealed class IdentityCheckRequestDto
        {
            [DataMember(Name = "phone")] public string Phone { get; set; }
            [DataMember(Name = "citizenId")] public string CitizenId { get; set; }
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
        private sealed class BorrowRequestListResponse
        {
            [DataMember(Name = "requests")] public List<BorrowRequestSummary> Requests { get; set; }
        }
        [DataContract]
        private sealed class NewBorrowRequestDto
        {
            [DataMember(Name = "items")] public List<BorrowRequestLine> Items { get; set; }
            [DataMember(Name = "note")] public string Note { get; set; }
        }
        [DataContract]
        private sealed class LoanBookListResponse
        {
            [DataMember(Name = "books")] public List<LoanBookSuggestion> Books { get; set; }
        }
        [DataContract]
        private sealed class LoanReaderListResponse
        {
            [DataMember(Name = "readers")] public List<LoanReaderSuggestion> Readers { get; set; }
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
    [DataContract]
    public sealed class LoanReaderSuggestion
    {
        [DataMember(Name = "username")] public string Username { get; set; }
        [DataMember(Name = "fullName")] public string FullName { get; set; }
        [DataMember(Name = "readerCode")] public string ReaderCode { get; set; }
        // User-facing display only; sending checkout uses Username, never FullName.
        public string Caption => (FullName ?? "") + "  •  @" + (Username ?? "") + "  •  " + (ReaderCode ?? "");
    }
    [DataContract]
    public sealed class LoanBookSuggestion
    {
        [DataMember(Name = "bookId")] public long BookId { get; set; }
        [DataMember(Name = "title")] public string Title { get; set; }
        [DataMember(Name = "isbn")] public string Isbn { get; set; }
        [DataMember(Name = "authors")] public string Authors { get; set; }
        [DataMember(Name = "categories")] public string Categories { get; set; }
        [DataMember(Name = "publisher")] public string Publisher { get; set; }
        [DataMember(Name = "availableCopies")] public long AvailableCopies { get; set; }
        [DataMember(Name = "firstAvailableCopyCode")] public string FirstAvailableCopyCode { get; set; }
        public string Metadata => string.IsNullOrWhiteSpace(Authors) ? "Chưa có tác giả" : Authors;
        public string AvailabilityText => AvailableCopies > 0
            ? "Còn " + AvailableCopies + " cuốn có thể mượn"
            : "Chưa có cuốn để mượn";
    }
    public sealed class LoanBookListResult
    {
        public bool IsSuccess { get; set; }
        public string Message { get; set; }
        public List<LoanBookSuggestion> Books { get; set; } = new List<LoanBookSuggestion>();
    }
    public sealed class LoanReaderListResult
    {
        public bool IsSuccess { get; set; }
        public string Message { get; set; }
        public List<LoanReaderSuggestion> Readers { get; set; } = new List<LoanReaderSuggestion>();
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

    // Requesting a title is not a checkout. All DTOs are kept in the EXISTING LoanApiClient.cs.
    [DataContract]
    public sealed class BorrowRequestLine
    {
        [DataMember(Name = "bookId")] public long BookId { get; set; }
        [DataMember(Name = "requestedDays")] public int RequestedDays { get; set; }
    }
    public sealed class BorrowRequestCartItem
    {
        public long BookId { get; set; }
        public string Title { get; set; }
        public string Authors { get; set; }
        public long AvailableCopies { get; set; }
        public int RequestedDays { get; set; }
        public string StockText => AvailableCopies > 0
            ? "Hiện còn " + AvailableCopies + " cuốn" : "Chưa có cuốn trong kho";
    }
    [DataContract]
    public sealed class BorrowRequestSummary
    {
        [DataMember(Name = "requestId")] public long RequestId { get; set; }
        [DataMember(Name = "requestCode")] public string RequestCode { get; set; }
        [DataMember(Name = "readerUsername")] public string ReaderUsername { get; set; }
        [DataMember(Name = "origin")] public string Origin { get; set; }
        public string OriginLabel => Origin == "staff_walkin" ? "Tại quầy" : "Qua app";
        [DataMember(Name = "status")] public string Status { get; set; }
        [DataMember(Name = "requestedAt")] public string RequestedAt { get; set; }
        [DataMember(Name = "note")] public string Note { get; set; }
        [DataMember(Name = "itemCount")] public long ItemCount { get; set; }
        [DataMember(Name = "itemsSummary")] public string ItemsSummary { get; set; }
        [DataMember(Name = "readerFullName")] public string ReaderFullName { get; set; }
        [DataMember(Name = "readerUserId")] public long ReaderUserId { get; set; }
        [DataMember(Name = "lateReturns")] public long LateReturns { get; set; }
        [DataMember(Name = "openOverdue")] public long OpenOverdue { get; set; }
        [DataMember(Name = "riskLevel")] public string RiskLevel { get; set; }
        [DataMember(Name = "completedReturns")] public long CompletedReturns { get; set; }
        public string RiskLabel => OpenOverdue > 0 ? "● ĐANG QUÁ HẠN (" + OpenOverdue + ")"
            : LateReturns >= 1 && LateReturns <= 2 ? "● TRẢ TRỄ " + LateReturns + " LẦN"
            : LateReturns > 2 ? "● TRẢ TRỄ NHIỀU (" + LateReturns + ")"
            : CompletedReturns > 0 ? "● TRẢ ĐÚNG HẠN" : "● CHƯA CÓ LỊCH SỬ";
        public bool CanCancel => Status == "pending";
    }
    public sealed class BorrowRequestListResult
    {
        public bool IsSuccess { get; set; }
        public string Message { get; set; }
        public List<BorrowRequestSummary> Requests { get; set; } = new List<BorrowRequestSummary>();
    }

    [DataContract]
    public sealed class IdentityCheckResult
    {
        public bool IsSuccess { get; set; }
        [DataMember(Name = "matches")] public List<ReaderIdentityMatch> Matches { get; set; } = new List<ReaderIdentityMatch>();
        [DataMember(Name = "matchedCount")] public int MatchedCount { get; set; }
        [DataMember(Name = "conflict")] public bool Conflict { get; set; }
        [DataMember(Name = "message")] public string Message { get; set; }
    }
    [DataContract]
    public sealed class ReaderIdentityMatch
    {
        [DataMember(Name = "userId")] public long UserId { get; set; }
        [DataMember(Name = "username")] public string Username { get; set; }
        [DataMember(Name = "readerCode")] public string ReaderCode { get; set; }
        [DataMember(Name = "fullName")] public string FullName { get; set; }
        [DataMember(Name = "accountStatus")] public string AccountStatus { get; set; }
        [DataMember(Name = "matchReason")] public string MatchReason { get; set; }
        [DataMember(Name = "openCopies")] public long OpenCopies { get; set; }
        [DataMember(Name = "overdueCopies")] public long OverdueCopies { get; set; }
        [DataMember(Name = "lateReturns")] public long LateReturns { get; set; }
        [DataMember(Name = "completedReturns")] public long CompletedReturns { get; set; }
        [DataMember(Name = "borrowBlocked")] public bool BorrowBlocked { get; set; }
        [DataMember(Name = "riskLevel")] public string RiskLevel { get; set; }
        public string Summary => FullName + " · " + ReaderCode + " · " + MatchReason;
        public string RiskSummary => (BorrowBlocked ? "ĐANG BỊ CẤM · " : "") +
            "Đang mượn: " + OpenCopies + " · Quá hạn chưa trả: " + OverdueCopies +
            " · Đã trả trễ: " + LateReturns + " · Đã trả: " + CompletedReturns;
    }

}
