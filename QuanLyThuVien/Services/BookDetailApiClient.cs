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
}
