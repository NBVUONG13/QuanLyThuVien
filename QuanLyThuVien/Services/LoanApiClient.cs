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
        private static readonly string BaseUrl = global::QuanLyThuVien.App.ApiBaseUrl + "/api/loans";
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };

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


        // Step 66 — Reader/Staff share renewal requests API, without touching loan checkout.
        public async Task<LoanRenewalListResult> GetLoanRenewalsAsync(string token)
        {
            try
            {
                using (var req = NewRequest(HttpMethod.Get, global::QuanLyThuVien.App.ApiBaseUrl + "/api/loan-renewals", token))
                using (var response = await Http.SendAsync(req))
                {
                    var json = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                        return new LoanRenewalListResult { Message = Error(json, response.StatusCode) };
                    var parsed = Parse<LoanRenewalListResponse>(json);
                    return new LoanRenewalListResult
                    {
                        IsSuccess = true,
                        Requests = parsed != null && parsed.Requests != null ? parsed.Requests : new List<LoanRenewalItem>()
                    };
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException ||
                                       ex is SerializationException || ex is InvalidOperationException)
            { return new LoanRenewalListResult { Message = "Không tải được danh sách gia hạn." }; }
        }

        public Task<LoanActionResult> RequestLoanRenewalAsync(string token, long borrowDetailId)
        {
            return SendActionAsync(global::QuanLyThuVien.App.ApiBaseUrl + "/api/loan-renewals/request", token,
                Serialize(new NewRenewalDto { BorrowDetailId = borrowDetailId }));
        }

        public Task<LoanActionResult> ReviewLoanRenewalAsync(string token, long requestId, bool approve, string note)
        {
            return SendActionAsync(global::QuanLyThuVien.App.ApiBaseUrl + "/api/loan-renewals/" + requestId + "/review",
                token, Serialize(new ReviewRequestDto { Approve = approve, Note = note }));
        }

        public Task<LoanActionResult> CancelLoanRenewalAsync(string token, long requestId)
        {
            return SendActionAsync(global::QuanLyThuVien.App.ApiBaseUrl + "/api/loan-renewals/" + requestId + "/cancel", token, "{}");
        }

        [DataContract]
        private sealed class NewRenewalDto
        {
            [DataMember(Name = "borrowDetailId")] public long BorrowDetailId { get; set; }
        }
        [DataContract]
        private sealed class LoanRenewalListResponse
        {
            [DataMember(Name = "requests")] public List<LoanRenewalItem> Requests { get; set; }
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
            string url = global::QuanLyThuVien.App.ApiBaseUrl + "/api/borrow-requests/books?query=" +
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
                           global::QuanLyThuVien.App.ApiBaseUrl + "/api/borrow-requests", token))
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
            return SendActionAsync(global::QuanLyThuVien.App.ApiBaseUrl + "/api/borrow-requests", token,
                Serialize(new NewBorrowRequestDto { Items = items, Note = note }));
        }

        public Task<LoanActionResult> CancelBorrowRequestAsync(string token, long requestId)
        {
            return SendActionAsync(global::QuanLyThuVien.App.ApiBaseUrl + "/api/borrow-requests/" +
                                   requestId + "/cancel", token, "{}");
        }

        public Task<LoanActionResult> ReviewBorrowRequestAsync(string token, long requestId, bool approve, string note)
        {
            return SendActionAsync(global::QuanLyThuVien.App.ApiBaseUrl + "/api/borrow-requests/" + requestId + "/review",
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
            return SendActionAsync(global::QuanLyThuVien.App.ApiBaseUrl + "/api/borrow-requests/walkin", token,
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
                    global::QuanLyThuVien.App.ApiBaseUrl + "/api/readers/identity-check", token))
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

        // STEP 85: Reader-only idempotent server synchronization of due reminders.
        // This uses the existing authenticated HTTP client and does not access SQL from WPF.
        public Task<LoanActionResult> SyncDueRemindersAsync(string token)
        {
            return SendActionAsync(global::QuanLyThuVien.App.ApiBaseUrl + "/api/notifications/sync-due", token, "{}");
        }

        // STEP 71: Read-only notifications + mark-as-read. No client-side SQL.
        public async Task<LibraryNotificationListResult> GetNotificationsAsync(string token)
        {
            try
            {
                using (var request = NewRequest(HttpMethod.Get, global::QuanLyThuVien.App.ApiBaseUrl + "/api/notifications", token))
                using (var response = await Http.SendAsync(request))
                {
                    string json = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                        return new LibraryNotificationListResult { Message = Error(json, response.StatusCode) };
                    var payload = Parse<LibraryNotificationResponse>(json);
                    return new LibraryNotificationListResult
                    {
                        IsSuccess = true,
                        UnreadCount = payload == null ? 0 : payload.UnreadCount,
                        Notifications = payload == null || payload.Notifications == null
                            ? new List<LibraryNotification>() : payload.Notifications
                    };
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException ||
                                       ex is SerializationException || ex is InvalidOperationException)
            { return new LibraryNotificationListResult { Message = "Không tải được thông báo. Kiểm tra API." }; }
        }

        public Task<LoanActionResult> MarkNotificationReadAsync(string token, long notificationId)
        {
            return SendActionAsync(global::QuanLyThuVien.App.ApiBaseUrl + "/api/notifications/" + notificationId + "/read", token, "{}");
        }
        public Task<LoanActionResult> MarkAllNotificationsReadAsync(string token)
        {
            return SendActionAsync(global::QuanLyThuVien.App.ApiBaseUrl + "/api/notifications/read-all", token, "{}");
        }

        [DataContract]
        private sealed class LibraryNotificationResponse
        {
            [DataMember(Name = "unreadCount")] public long UnreadCount { get; set; }
            [DataMember(Name = "notifications")] public List<LibraryNotification> Notifications { get; set; }
        }

        // STEP 72: fine balances and real staff payment records. The WPF never queries SQL.
        public async Task<LibraryFineListResult> GetFinesAsync(string token, string query)
        {
            try
            {
                string url = global::QuanLyThuVien.App.ApiBaseUrl + "/api/fines?query=" + Uri.EscapeDataString(query ?? "");
                using (var req = NewRequest(HttpMethod.Get, url, token))
                using (var resp = await Http.SendAsync(req))
                {
                    var json = await resp.Content.ReadAsStringAsync();
                    if (!resp.IsSuccessStatusCode)
                        return new LibraryFineListResult { Message = Error(json, resp.StatusCode) };
                    var parsed = Parse<FineListResponse>(json);
                    return new LibraryFineListResult
                    {
                        IsSuccess = true,
                        Fines = parsed == null || parsed.Fines == null ? new List<LibraryFine>() : parsed.Fines
                    };
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException ||
                                       ex is SerializationException || ex is InvalidOperationException)
            { return new LibraryFineListResult { Message = "Không tải được danh sách tiền phạt." }; }
        }
        public async Task<LibraryFinePaymentListResult> GetFinePaymentsAsync(string token, long fineId)
        {
            try
            {
                string url = global::QuanLyThuVien.App.ApiBaseUrl + "/api/fines/" + fineId + "/payments";
                using (var req = NewRequest(HttpMethod.Get, url, token))
                using (var resp = await Http.SendAsync(req))
                {
                    var json = await resp.Content.ReadAsStringAsync();
                    if (!resp.IsSuccessStatusCode)
                        return new LibraryFinePaymentListResult { Message = Error(json, resp.StatusCode) };
                    var parsed = Parse<FinePaymentsResponse>(json);
                    return new LibraryFinePaymentListResult
                    {
                        IsSuccess = true,
                        Payments = parsed == null || parsed.Payments == null
                            ? new List<LibraryFinePayment>() : parsed.Payments
                    };
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException ||
                                       ex is SerializationException || ex is InvalidOperationException)
            { return new LibraryFinePaymentListResult { Message = "Không tải được lịch sử thanh toán." }; }
        }
        public Task<LoanActionResult> PayFineAsync(string token, long fineId, decimal amount,
            string method, string reference)
        {
            return SendActionAsync(global::QuanLyThuVien.App.ApiBaseUrl + "/api/fines/" + fineId + "/payments", token,
                Serialize(new FinePaymentDto { Amount = amount, Method = method, TransactionReference = reference }));
        }
        [DataContract]
        private sealed class FinePaymentDto
        {
            [DataMember(Name = "amount")] public decimal Amount { get; set; }
            [DataMember(Name = "method")] public string Method { get; set; }
            [DataMember(Name = "transactionReference")] public string TransactionReference { get; set; }
        }
        [DataContract]
        private sealed class FineListResponse
        {
            [DataMember(Name = "fines")] public List<LibraryFine> Fines { get; set; }
        }
        [DataContract]
        private sealed class FinePaymentsResponse
        {
            [DataMember(Name = "payments")] public List<LibraryFinePayment> Payments { get; set; }
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
    public sealed class LoanRenewalItem
    {
        [DataMember(Name = "requestId")] public long RequestId { get; set; }
        [DataMember(Name = "borrowDetailId")] public long BorrowDetailId { get; set; }
        [DataMember(Name = "readerUsername")] public string ReaderUsername { get; set; }
        [DataMember(Name = "copyCode")] public string CopyCode { get; set; }
        [DataMember(Name = "bookTitle")] public string BookTitle { get; set; }
        [DataMember(Name = "status")] public string Status { get; set; }
        [DataMember(Name = "requestedAt")] public string RequestedAt { get; set; }
        [DataMember(Name = "currentDueAt")] public string CurrentDueAt { get; set; }
        [DataMember(Name = "reviewNote")] public string ReviewNote { get; set; }
        [DataMember(Name = "isOpen")] public bool IsOpen { get; set; }
    }
    public sealed class LoanRenewalListResult
    {
        public bool IsSuccess { get; set; }
        public string Message { get; set; }
        public List<LoanRenewalItem> Requests { get; set; } = new List<LoanRenewalItem>();
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

    [DataContract]
    public sealed class LibraryFine
    {
        [DataMember(Name = "fineId")] public long FineId { get; set; }
        [DataMember(Name = "borrowDetailId")] public long BorrowDetailId { get; set; }
        [DataMember(Name = "readerUsername")] public string ReaderUsername { get; set; }
        [DataMember(Name = "readerCode")] public string ReaderCode { get; set; }
        [DataMember(Name = "readerName")] public string ReaderName { get; set; }
        [DataMember(Name = "bookTitle")] public string BookTitle { get; set; }
        [DataMember(Name = "copyCode")] public string CopyCode { get; set; }
        [DataMember(Name = "fineType")] public string FineType { get; set; }
        [DataMember(Name = "amount")] public decimal Amount { get; set; }
        [DataMember(Name = "waivedAmount")] public decimal WaivedAmount { get; set; }
        [DataMember(Name = "paidAmount")] public decimal PaidAmount { get; set; }
        [DataMember(Name = "outstanding")] public decimal Outstanding { get; set; }
        [DataMember(Name = "assessedAt")] public string AssessedAt { get; set; }
        public string ReaderLabel => (ReaderName ?? "") + " · " + (ReaderCode ?? "");
        public string FineTypeLabel => FineType == "overdue" ? "Quá hạn" :
            FineType == "lost" ? "Mất sách" : "Hỏng sách";
        public string StatusLabel => Outstanding > 0 ? "Còn nợ" : "Đã tất toán";
    }
    public sealed class LibraryFineListResult
    {
        public bool IsSuccess { get; set; }
        public string Message { get; set; }
        public List<LibraryFine> Fines { get; set; } = new List<LibraryFine>();
    }
    [DataContract]
    public sealed class LibraryFinePayment
    {
        [DataMember(Name = "paymentId")] public long PaymentId { get; set; }
        [DataMember(Name = "amount")] public decimal Amount { get; set; }
        [DataMember(Name = "method")] public string Method { get; set; }
        [DataMember(Name = "reference")] public string Reference { get; set; }
        [DataMember(Name = "paidAt")] public string PaidAt { get; set; }
        public string MethodLabel => Method == "cash" ? "Tiền mặt" : "Chuyển khoản";
    }
    public sealed class LibraryFinePaymentListResult
    {
        public bool IsSuccess { get; set; }
        public string Message { get; set; }
        public List<LibraryFinePayment> Payments { get; set; } = new List<LibraryFinePayment>();
    }

    [DataContract]
    public sealed class LibraryNotification
    {
        [DataMember(Name = "notificationId")] public long NotificationId { get; set; }
        [DataMember(Name = "notificationType")] public string NotificationType { get; set; }
        [DataMember(Name = "title")] public string Title { get; set; }
        [DataMember(Name = "body")] public string Body { get; set; }
        [DataMember(Name = "createdAt")] public string CreatedAt { get; set; }
        [DataMember(Name = "isUnread")] public bool IsUnread { get; set; }
        public string ReadStatus => IsUnread ? "CHƯA ĐỌC" : "Đã đọc";
    }
    public sealed class LibraryNotificationListResult
    {
        public bool IsSuccess { get; set; }
        public string Message { get; set; }
        public long UnreadCount { get; set; }
        public List<LibraryNotification> Notifications { get; set; } = new List<LibraryNotification>();
    }
}
