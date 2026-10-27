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
    public sealed class DashboardApiClient
    {
        private static readonly HttpClient Http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        public async Task<DashboardSummaryResult> LoadAsync(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return DashboardSummaryResult.Fail("Chưa có phiên đăng nhập.");
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Get,
                    "http://127.0.0.1:5082/api/dashboard/summary"))
                {
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    using (var response = await Http.SendAsync(req).ConfigureAwait(false))
                    {
                        if (response.StatusCode == HttpStatusCode.Unauthorized)
                            return DashboardSummaryResult.Fail("Phiên hết hạn, vui lòng đăng nhập lại.");
                        if (response.StatusCode == HttpStatusCode.Forbidden)
                            return DashboardSummaryResult.Fail("Bạn không được phép xem thống kê.");
                        if (!response.IsSuccessStatusCode)
                            return DashboardSummaryResult.Fail("Không đọc được thống kê từ API (HTTP " +
                                (int)response.StatusCode + ").");
                        string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var serializer = new DataContractJsonSerializer(typeof(DashboardSummaryDto));
                        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                        {
                            var data = (DashboardSummaryDto)serializer.ReadObject(stream);
                            return DashboardSummaryResult.Ok(data.Books, data.Authors,
                                data.Categories, data.PhysicalCopies, data.Readers);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException ||
                                       ex is TaskCanceledException ||
                                       ex is SerializationException ||
                                       ex is InvalidCastException)
            {
                return DashboardSummaryResult.Fail("Không kết nối hoặc đọc được thống kê từ API.");
            }
        }

        [DataContract]
        private sealed class DashboardSummaryDto
        {
            [DataMember(Name = "books")] public long Books { get; set; }
            [DataMember(Name = "authors")] public long Authors { get; set; }
            [DataMember(Name = "categories")] public long Categories { get; set; }
            [DataMember(Name = "physicalCopies")] public long PhysicalCopies { get; set; }
            [DataMember(Name = "readers")] public long Readers { get; set; }
        }
    }

    public sealed class DashboardSummaryResult
    {
        public bool IsSuccess { get; private set; }
        public string Message { get; private set; }
        public long Books { get; private set; }
        public long Authors { get; private set; }
        public long Categories { get; private set; }
        public long PhysicalCopies { get; private set; }
        public long Readers { get; private set; }

        internal static DashboardSummaryResult Ok(long books, long authors, long categories,
            long physicalCopies, long readers)
        {
            return new DashboardSummaryResult
            {
                IsSuccess = true,
                Books = books,
                Authors = authors,
                Categories = categories,
                PhysicalCopies = physicalCopies,
                Readers = readers
            };
        }
        internal static DashboardSummaryResult Fail(string message)
        {
            return new DashboardSummaryResult { IsSuccess = false, Message = message };
        }
    }
}
