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

        // STEP 69: Location edits are restricted to Staff/Admin by the server.
        public async Task<string> ChangeCopyLocationAsync(string token, long bookId, long copyId, string shelfLocation)
        {
            try
            {
                var body = Serialize(new ChangeLocationPayload { ShelfLocation = shelfLocation });
                using (var request = Authorized(HttpMethod.Put, Api + bookId + "/copies/" + copyId + "/location", token))
                {
                    request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    using (var response = await Http.SendAsync(request))
                    {
                        if (response.IsSuccessStatusCode) return "Đã cập nhật vị trí cuốn sách.";
                        if (response.StatusCode == HttpStatusCode.Forbidden) return "Tài khoản không được sửa vị trí sách.";
                        if (response.StatusCode == HttpStatusCode.Conflict) return "Không thể đổi vị trí: cuốn có thể đang được mượn.";
                        if (response.StatusCode == HttpStatusCode.BadRequest) return "Vị trí không hợp lệ. Chọn kệ K1-K6, tầng 1-4 và ô A1-D4.";
                        return "Không lưu được vị trí (HTTP " + (int)response.StatusCode + ").";
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is SerializationException)
            { return "Không kết nối được API để lưu vị trí cuốn."; }
        }
        [DataContract]
        private sealed class ChangeLocationPayload
        {
            [DataMember(Name = "shelfLocation")] public string ShelfLocation { get; set; }
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
