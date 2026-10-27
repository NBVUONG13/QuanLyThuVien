using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading.Tasks;

namespace QuanLyThuVien.Services
{
    [DataContract]
    public sealed class CatalogBook
    {
        [DataMember(Name = "bookId")] public long BookId { get; set; }
        [DataMember(Name = "title")] public string Title { get; set; }
        [DataMember(Name = "isbn")] public string Isbn { get; set; }
        [DataMember(Name = "authors")] public string Authors { get; set; }
        [DataMember(Name = "publisher")] public string Publisher { get; set; }
        [DataMember(Name = "categories")] public string Categories { get; set; }
        [DataMember(Name = "publicationYear")] public int PublicationYear { get; set; }
        [DataMember(Name = "physicalCopies")] public long PhysicalCopies { get; set; }
    }

    [DataContract]
    internal sealed class CatalogEnvelope
    {
        [DataMember(Name = "books")] public List<CatalogBook> Books { get; set; }
    }

    public sealed class BookCatalogResult
    {
        public bool IsSuccess { get; private set; }
        public string Message { get; private set; }
        public List<CatalogBook> Books { get; private set; }
        internal static BookCatalogResult Ok(List<CatalogBook> books) => new BookCatalogResult
        { IsSuccess = true, Books = books ?? new List<CatalogBook>(), Message = "OK" };
        internal static BookCatalogResult Fail(string text) => new BookCatalogResult
        { IsSuccess = false, Books = new List<CatalogBook>(), Message = text };
    }

    public sealed class BookCatalogApiClient
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        public async Task<BookCatalogResult> SearchAsync(string token, string query)
        {
            if (string.IsNullOrWhiteSpace(token)) return BookCatalogResult.Fail("Chua dang nhap.");
            query = (query ?? "").Trim();
            if (query.Length > 100) return BookCatalogResult.Fail("Tu khoa toi da 100 ky tu.");
            string url = "http://127.0.0.1:5082/api/books?q=" + Uri.EscapeDataString(query);
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    request.Headers.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                    using (var response = await Http.SendAsync(request))
                    {
                        if (response.StatusCode == HttpStatusCode.Unauthorized)
                            return BookCatalogResult.Fail("Phien het han, vui long dang nhap lai.");
                        if (!response.IsSuccessStatusCode)
                            return BookCatalogResult.Fail("API loi (HTTP " + (int)response.StatusCode + ").");
                        var json = await response.Content.ReadAsStringAsync();
                        var serializer = new DataContractJsonSerializer(typeof(CatalogEnvelope));
                        using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
                        {
                            var data = (CatalogEnvelope)serializer.ReadObject(stream);
                            return BookCatalogResult.Ok(data == null ? null : data.Books);
                        }
                    }
                }
            }
            catch (HttpRequestException) { return BookCatalogResult.Fail("Khong ket noi duoc API."); }
            catch (TaskCanceledException) { return BookCatalogResult.Fail("API qua thoi gian cho."); }
            catch (SerializationException) { return BookCatalogResult.Fail("JSON tu API khong hop le."); }
        }
    }
}
