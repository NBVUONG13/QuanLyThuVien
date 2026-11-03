using System;
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
    // WPF client: database credentials never leave the API.
    public sealed class BookDetailApiClient
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        private const string Api = "http://127.0.0.1:5082/api/books/";

        public async Task<BookDetailResult> GetDetailsAsync(string token, long bookId)
        {
            try
            {
                using (var request = Authorized(HttpMethod.Get, Api + bookId + "/details", token))
                using (var response = await Http.SendAsync(request))
                {
                    if (response.StatusCode == HttpStatusCode.Unauthorized) return BookDetailResult.Fail("Phiên hết hạn; vui lòng đăng nhập lại.");
                    if (!response.IsSuccessStatusCode) return BookDetailResult.Fail("Không lấy được chi tiết sách (HTTP " + (int)response.StatusCode + ").");
                    var data = Deserialize<BookDetailResult>(await response.Content.ReadAsStringAsync());
                    return data ?? BookDetailResult.Fail("Không đọc được nội dung sách.");
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is SerializationException)
            { return BookDetailResult.Fail("Không kết nối được dịch vụ sách."); }
        }

        // STEP 70: Review endpoint is read-only for Staff/Admin; Reader can write with loan history.
        public async Task<BookRatingsResult> GetRatingsAsync(string token, long bookId)
        {
            try
            {
                using (var request = Authorized(HttpMethod.Get, Api + bookId + "/ratings", token))
                using (var response = await Http.SendAsync(request))
                {
                    if (response.StatusCode == HttpStatusCode.Unauthorized)
                        return BookRatingsResult.Fail("Phiên đăng nhập đã hết hạn.");
                    if (!response.IsSuccessStatusCode)
                        return BookRatingsResult.Fail("Không tải được đánh giá sách (HTTP " + (int)response.StatusCode + ").");
                    return Deserialize<BookRatingsResult>(await response.Content.ReadAsStringAsync())
                        ?? BookRatingsResult.Fail("Dữ liệu đánh giá không hợp lệ.");
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is SerializationException)
            { return BookRatingsResult.Fail("Không kết nối được API đánh giá sách."); }
        }

        public async Task<string> SaveRatingAsync(string token, long bookId, int score, string comment)
        {
            try
            {
                string json = Serialize(new BookRatingPayload { Score = score, Comment = comment });
                using (var request = Authorized(HttpMethod.Put, Api + bookId + "/rating", token))
                {
                    request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                    using (var response = await Http.SendAsync(request))
                    {
                        if (response.IsSuccessStatusCode) return "Đã lưu đánh giá sách.";
                        if (response.StatusCode == HttpStatusCode.Forbidden)
                            return "Bạn chỉ được đánh giá sách đã từng mượn bằng tài khoản Reader.";
                        if (response.StatusCode == HttpStatusCode.Unauthorized)
                            return "Phiên đăng nhập đã hết hạn.";
                        if (response.StatusCode == HttpStatusCode.BadRequest)
                            return "Điểm phải từ 1–5 và nhận xét không quá 1000 ký tự.";
                        return "Không lưu được đánh giá (HTTP " + (int)response.StatusCode + ").";
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is SerializationException)
            { return "Không kết nối được API đánh giá sách."; }
        }

        [DataContract]
        private sealed class BookRatingPayload
        {
            [DataMember(Name = "score")] public int Score { get; set; }
            [DataMember(Name = "comment")] public string Comment { get; set; }
        }

        public async Task<CopyListResult> GetCopiesAsync(string token, long bookId)
        {
            try
            {
                using (var request = Authorized(HttpMethod.Get, Api + bookId + "/copies", token))
                using (var response = await Http.SendAsync(request))
                {
                    if (response.StatusCode == HttpStatusCode.Forbidden) return CopyListResult.Fail("Chỉ Staff/Admin được quản lý cuốn vật lý.");
                    if (!response.IsSuccessStatusCode) return CopyListResult.Fail("Lỗi tải bản sao: HTTP " + (int)response.StatusCode);
                    return Deserialize<CopyListResult>(await response.Content.ReadAsStringAsync()) ?? CopyListResult.Fail("Dữ liệu không hợp lệ.");
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is SerializationException)
            { return CopyListResult.Fail("Không kết nối được API."); }
        }

        public async Task<string> AddCopyAsync(string token, long bookId, string copyCode, string shelfLocation)
        {
            try
            {
                var payload = Serialize(new AddCopyPayload { CopyCode = copyCode, ShelfLocation = shelfLocation });
                using (var request = Authorized(HttpMethod.Post, Api + bookId + "/copies", token))
                {
                    request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                    using (var response = await Http.SendAsync(request))
                    {
                        if (response.StatusCode == HttpStatusCode.Created) return "Đã thêm cuốn vật lý thành công.";
                        if (response.StatusCode == HttpStatusCode.Forbidden) return "Không có quyền thêm cuốn vật lý.";
                        if (response.StatusCode == HttpStatusCode.Conflict) return "Mã cuốn đã tồn tại.";
                        return "Không tạo được cuốn vật lý (HTTP " + (int)response.StatusCode + ").";
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is SerializationException)
            { return "Không kết nối được API."; }
        }


        // Step 77 - reuses the existing book API client; no extra WPF service or csproj entry.
        private const string Inventory = "http://127.0.0.1:5082/api/inventory/";

        public Task<InventoryBookListResponse> InventoryBooksAsync(string token) =>
            GetInventoryAsync<InventoryBookListResponse>(token, Inventory + "books");

        public Task<InventoryBookResponse> InventoryBookAsync(string token, long bookId) =>
            GetInventoryAsync<InventoryBookResponse>(token, Inventory + "books/" + bookId);

        public Task<InventoryCopyListResponse> InventoryCopiesAsync(string token, long bookId) =>
            GetInventoryAsync<InventoryCopyListResponse>(token, Inventory + "books/" + bookId + "/copies");

        public Task<InventoryActionResponse> SaveInventoryBookAsync(string token, InventoryBookEdit book)
        {
            var isNew = book.BookId <= 0;
            return ChangeInventoryAsync(token, isNew ? HttpMethod.Post : HttpMethod.Put,
                Inventory + "books" + (isNew ? "" : "/" + book.BookId), book);
        }

        public Task<InventoryActionResponse> ReceiveStockAsync(string token, long bookId, int quantity, string shelf) =>
            ChangeInventoryAsync(token, HttpMethod.Post, Inventory + "books/" + bookId + "/copies/batch",
                new InventoryReceiveRequest { Quantity = quantity, ShelfLocation = shelf });

        public Task<InventoryActionResponse> EditInventoryCopyAsync(string token, InventoryCopyEdit copy) =>
            ChangeInventoryAsync(token, HttpMethod.Put, Inventory + "copies/" + copy.CopyId, copy);

        public Task<InventoryActionResponse> ReportInventoryIncidentAsync(string token, long borrowDetailId,
            string kind, decimal amount, string note) =>
            ChangeInventoryAsync(token, HttpMethod.Post, Inventory + "loans/" + borrowDetailId + "/incident",
                new InventoryIncidentRequest { Kind = kind, CompensationAmount = amount, Note = note });

        private async Task<T> GetInventoryAsync<T>(string token, string url)
            where T : InventoryResponseBase, new()
        {
            try
            {
                using (var request = Authorized(HttpMethod.Get, url, token))
                using (var response = await Http.SendAsync(request))
                {
                    var json = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                        return new T { Error = InventoryError(response.StatusCode, json) };
                    return Deserialize<T>(json) ?? new T { Error = "API trả về dữ liệu trống." };
                }
            }
            catch (HttpRequestException)
            { return new T { Error = "Không kết nối được http://127.0.0.1:5082. Hãy chạy SmartLibrary.Api." }; }
            catch (TaskCanceledException)
            { return new T { Error = "Yêu cầu tải kho quá thời gian 20 giây. Kiểm tra API/Aiven." }; }
            catch (Exception ex)
            { return new T { Error = "Lỗi phân tích dữ liệu kho (" + ex.GetType().Name + ")." }; }
        }

        private async Task<InventoryActionResponse> ChangeInventoryAsync<T>(string token, HttpMethod method,
            string url, T body)
        {
            try
            {
                using (var request = Authorized(method, url, token))
                {
                    request.Content = new StringContent(Serialize(body), Encoding.UTF8, "application/json");
                    using (var response = await Http.SendAsync(request))
                    {
                        var json = await response.Content.ReadAsStringAsync();
                        if (!response.IsSuccessStatusCode)
                            return new InventoryActionResponse { Error = InventoryError(response.StatusCode, json) };
                        return Deserialize<InventoryActionResponse>(json) ??
                               new InventoryActionResponse { Error = "API không trả về kết quả." };
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException ||
                                       ex is SerializationException || ex is System.Xml.XmlException)
            { return new InventoryActionResponse { Error = "Không thể gửi dữ liệu lên API." }; }
        }

        private static string InventoryError(HttpStatusCode code, string json)
        {
            if (code == HttpStatusCode.Unauthorized) return "Phiên hết hạn, đăng nhập lại.";
            if (code == HttpStatusCode.Forbidden) return "Tài khoản không có quyền quản lý kho.";
            if (code == HttpStatusCode.NotFound) return "HTTP 404: API kho chưa được đăng ký; kiểm tra app.MapBookInventoryEndpoints().";
            if (code == HttpStatusCode.ServiceUnavailable)
                return "HTTP 503: Backend không đọc được kho từ PostgreSQL. Xem console SmartLibrary.Api để biết SQLSTATE.";
            try
            {
                var item = Deserialize<InventoryActionResponse>(json);
                if (!string.IsNullOrWhiteSpace(item?.Message)) return item.Message;
            }
            catch (Exception) { }
            return "API từ chối yêu cầu (HTTP " + (int)code + ").";
        }

        private static HttpRequestMessage Authorized(HttpMethod method, string url, string token)
        {
            var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return request;
        }
        private static T Deserialize<T>(string json)
        {
            var ser = new DataContractJsonSerializer(typeof(T));
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json))) return (T)ser.ReadObject(ms);
        }
        private static string Serialize<T>(T value)
        {
            var ser = new DataContractJsonSerializer(typeof(T));
            using (var ms = new MemoryStream()) { ser.WriteObject(ms, value); return Encoding.UTF8.GetString(ms.ToArray()); }
        }
        [DataContract]
        private sealed class AddCopyPayload
        {
            [DataMember(Name = "copyCode")] public string CopyCode { get; set; }
            [DataMember(Name = "shelfLocation")] public string ShelfLocation { get; set; }
        }
    }

    // STEP 70: Reader ratings are scoped to one book; usernames are deliberately omitted.
    [DataContract]
    public sealed class BookRatingsResult
    {
        [DataMember(Name = "ratingCount")] public long RatingCount { get; set; }
        [DataMember(Name = "averageScore")] public double AverageScore { get; set; }
        [DataMember(Name = "canReview")] public bool CanReview { get; set; }
        [DataMember(Name = "myScore")] public int MyScore { get; set; }
        [DataMember(Name = "myComment")] public string MyComment { get; set; }
        [DataMember(Name = "reviews")] public System.Collections.Generic.List<BookRatingItem> Reviews { get; set; }
        public string Error { get; set; }
        public static BookRatingsResult Fail(string error) { return new BookRatingsResult { Error = error }; }
    }

    [DataContract]
    public sealed class BookRatingItem
    {
        [DataMember(Name = "score")] public int Score { get; set; }
        [DataMember(Name = "comment")] public string Comment { get; set; }
        [DataMember(Name = "updatedAt")] public string UpdatedAt { get; set; }
        public string ScoreLabel => Score.ToString() + "/5 sao · " + (UpdatedAt ?? "");
    }

    [DataContract]
    public sealed class BookDetailResult
    {
        [DataMember(Name = "bookId")] public long BookId { get; set; }
        [DataMember(Name = "title")] public string Title { get; set; }
        [DataMember(Name = "isbn")] public string Isbn { get; set; }
        [DataMember(Name = "description")] public string Description { get; set; }
        [DataMember(Name = "publisher")] public string Publisher { get; set; }
        [DataMember(Name = "authors")] public string Authors { get; set; }
        [DataMember(Name = "categories")] public string Categories { get; set; }
        [DataMember(Name = "publicationYear")] public int PublicationYear { get; set; }
        [DataMember(Name = "totalCopies")] public long TotalCopies { get; set; }
        [DataMember(Name = "activeCopies")] public long ActiveCopies { get; set; }
        [DataMember(Name = "availableCopies")] public long AvailableCopies { get; set; }
        [DataMember(Name = "onLoanCopies")] public long OnLoanCopies { get; set; }
        [DataMember(Name = "coverUrl")] public string CoverUrl { get; set; }
        public string PhysicalStockStatus => TotalCopies > 0
            ? "Đã nhập " + TotalCopies + " bản vật lý" : "Chưa nhập bản vật lý; chưa có cuốn để mượn ngay.";
        public string Error { get; set; }
        public static BookDetailResult Fail(string error) { return new BookDetailResult { Error = error }; }
    }
    [DataContract]
    public sealed class CopyListResult
    {
        [DataMember(Name = "copies")] public System.Collections.Generic.List<BookCopyDto> Copies { get; set; }
        public string Error { get; set; }
        public static CopyListResult Fail(string error) { return new CopyListResult { Error = error }; }
    }
    [DataContract]
    public sealed class BookCopyDto
    {
        [DataMember(Name = "copyId")] public long CopyId { get; set; }
        [DataMember(Name = "copyCode")] public string CopyCode { get; set; }
        [DataMember(Name = "conditionStatus")] public string ConditionStatus { get; set; }
        [DataMember(Name = "shelfLocation")] public string ShelfLocation { get; set; }
    }
    // DTOs live beside BookDetailApiClient to keep step 77 at four source files.
    // A data-contract-derived type must also have a serializable base type.
    [DataContract]
    public class InventoryResponseBase
    {
        public string Error { get; set; }
    }

    [DataContract]
    public sealed class InventoryBookListResponse : InventoryResponseBase
    {
        [DataMember(Name = "books")] public System.Collections.Generic.List<InventoryBookRow> Books { get; set; }
    }

    [DataContract]
    public sealed class InventoryBookRow
    {
        [DataMember(Name = "bookId")] public long BookId { get; set; }
        [DataMember(Name = "title")] public string Title { get; set; }
        [DataMember(Name = "isbn")] public string Isbn { get; set; }
        [DataMember(Name = "publicationYear")] public int PublicationYear { get; set; }
        [DataMember(Name = "publisher")] public string Publisher { get; set; }
        [DataMember(Name = "isVisible")] public bool IsVisible { get; set; }
        [DataMember(Name = "totalCopies")] public long TotalCopies { get; set; }
        [DataMember(Name = "availableCopies")] public long AvailableCopies { get; set; }
        [DataMember(Name = "onLoanCopies")] public long OnLoanCopies { get; set; }
        public string VisibleText { get { return IsVisible ? "Hiển thị" : "Đã ẩn"; } }
    }

    [DataContract]
    public sealed class InventoryBookResponse : InventoryResponseBase
    {
        [DataMember(Name = "book")] public InventoryBookEdit Book { get; set; }
    }

    [DataContract]
    public sealed class InventoryBookEdit
    {
        [DataMember(Name = "bookId")] public long BookId { get; set; }
        [DataMember(Name = "title")] public string Title { get; set; }
        [DataMember(Name = "isbn")] public string Isbn { get; set; }
        [DataMember(Name = "description")] public string Description { get; set; }
        [DataMember(Name = "publicationYear")] public int? PublicationYear { get; set; }
        [DataMember(Name = "publisherId")] public long? PublisherId { get; set; }
        [DataMember(Name = "authorIds")] public System.Collections.Generic.List<long> AuthorIds { get; set; }
        [DataMember(Name = "categoryIds")] public System.Collections.Generic.List<long> CategoryIds { get; set; }
        [DataMember(Name = "isVisible")] public bool IsVisible { get; set; }
    }

    [DataContract]
    public sealed class InventoryCopyListResponse : InventoryResponseBase
    {
        [DataMember(Name = "copies")] public System.Collections.Generic.List<InventoryCopyRow> Copies { get; set; }
    }

    [DataContract]
    public sealed class InventoryCopyRow
    {
        [DataMember(Name = "copyId")] public long CopyId { get; set; }
        [DataMember(Name = "copyCode")] public string CopyCode { get; set; }
        [DataMember(Name = "conditionStatus")] public string ConditionStatus { get; set; }
        [DataMember(Name = "shelfLocation")] public string ShelfLocation { get; set; }
        [DataMember(Name = "conditionNote")] public string ConditionNote { get; set; }
        [DataMember(Name = "onLoan")] public bool OnLoan { get; set; }
        [DataMember(Name = "openBorrowDetailId")] public long? OpenBorrowDetailId { get; set; }
        public string LoanText { get { return OnLoan ? "Đang mượn #" + OpenBorrowDetailId : "Trong kho"; } }
    }

    [DataContract]
    public sealed class InventoryActionResponse : InventoryResponseBase
    {
        [DataMember(Name = "message")] public string Message { get; set; }
        [DataMember(Name = "bookId")] public long BookId { get; set; }
        [DataMember(Name = "quantity")] public int Quantity { get; set; }
    }

    [DataContract]
    internal sealed class InventoryReceiveRequest
    {
        [DataMember(Name = "quantity")] public int Quantity { get; set; }
        [DataMember(Name = "shelfLocation")] public string ShelfLocation { get; set; }
    }

    [DataContract]
    public sealed class InventoryCopyEdit
    {
        [DataMember(Name = "copyId")] public long CopyId { get; set; }
        [DataMember(Name = "conditionStatus")] public string ConditionStatus { get; set; }
        [DataMember(Name = "shelfLocation")] public string ShelfLocation { get; set; }
        [DataMember(Name = "conditionNote")] public string ConditionNote { get; set; }
    }

    [DataContract]
    internal sealed class InventoryIncidentRequest
    {
        [DataMember(Name = "kind")] public string Kind { get; set; }
        [DataMember(Name = "compensationAmount")] public decimal CompensationAmount { get; set; }
        [DataMember(Name = "note")] public string Note { get; set; }
    }

    // STEP 68: Standalone HTTP client for location-only data. No database secrets in WPF.
    [DataContract]
    public sealed class LibraryMapItem
    {
        [DataMember(Name = "bookId")] public long BookId { get; set; }
        [DataMember(Name = "title")] public string Title { get; set; }
        [DataMember(Name = "shelfLocation")] public string ShelfLocation { get; set; }
        [DataMember(Name = "totalCopies")] public long TotalCopies { get; set; }
        [DataMember(Name = "availableCopies")] public long AvailableCopies { get; set; }
        [DataMember(Name = "sampleCopyCode")] public string SampleCopyCode { get; set; }
        public string Label => (Title ?? "") + "  |  " + (ShelfLocation ?? "") + "  |  Còn " + AvailableCopies;
    }

    [DataContract]
    internal sealed class LibraryMapEnvelope
    {
        [DataMember(Name = "items")] public System.Collections.Generic.List<LibraryMapItem> Items { get; set; }
    }

    public sealed class LibraryMapApiClient
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        public async Task<LibraryMapEnvelopeResult> GetAsync(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return LibraryMapEnvelopeResult.Fail("Chưa đăng nhập.");
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:5082/api/library-map/books"))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    using (var response = await Http.SendAsync(request))
                    {
                        if (response.StatusCode == HttpStatusCode.Unauthorized) return LibraryMapEnvelopeResult.Fail("Phiên đã hết hạn.");
                        if (!response.IsSuccessStatusCode)
                            return LibraryMapEnvelopeResult.Fail("Không tải được sơ đồ sách (HTTP " + (int)response.StatusCode + ").");
                        var serializer = new DataContractJsonSerializer(typeof(LibraryMapEnvelope));
                        using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(await response.Content.ReadAsStringAsync())))
                        {
                            var data = (LibraryMapEnvelope)serializer.ReadObject(ms);
                            return LibraryMapEnvelopeResult.Ok(data == null ? null : data.Items);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is SerializationException)
            { return LibraryMapEnvelopeResult.Fail("Không kết nối được API vị trí kệ."); }
        }
    }

    public sealed class LibraryMapEnvelopeResult
    {
        public System.Collections.Generic.List<LibraryMapItem> Items { get; private set; }
        public string Error { get; private set; }
        public static LibraryMapEnvelopeResult Ok(System.Collections.Generic.List<LibraryMapItem> items) =>
            new LibraryMapEnvelopeResult { Items = items ?? new System.Collections.Generic.List<LibraryMapItem>() };
        public static LibraryMapEnvelopeResult Fail(string message) =>
            new LibraryMapEnvelopeResult { Items = new System.Collections.Generic.List<LibraryMapItem>(), Error = message };
    }

}
