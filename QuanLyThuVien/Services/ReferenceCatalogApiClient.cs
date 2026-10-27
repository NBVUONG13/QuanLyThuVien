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
    public sealed class ReferenceCatalogApiClient
    {
        private const string Root = "http://127.0.0.1:5082/api/catalog/reference";
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };

        public async Task<ReferenceListResult> GetAsync(string token)
        {
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, Root))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    using (var response = await Http.SendAsync(request))
                    {
                        string json = await response.Content.ReadAsStringAsync();
                        if (!response.IsSuccessStatusCode) return new ReferenceListResult { Message = "Không tải được danh mục (HTTP " + (int)response.StatusCode + ")." };
                        var result = Decode<ReferenceListResult>(json);
                        result.IsSuccess = true;
                        return result;
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is SerializationException)
            {
                return new ReferenceListResult { Message = "Không kết nối được API hoặc không đọc được dữ liệu." };
            }
        }

        public async Task<string> AddAsync(string token, string kind, string name, string detail)
        {
            if (kind != "authors" && kind != "publishers" && kind != "categories")
                return "Loại dữ liệu không hợp lệ.";
            try
            {
                var json = Encode(new ReferenceNewItem { Name = name, Detail = detail });
                using (var request = new HttpRequestMessage(HttpMethod.Post, Root + "/" + kind))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                    using (var response = await Http.SendAsync(request))
                    {
                        if (response.StatusCode == HttpStatusCode.Forbidden)
                            return "Không có quyền thêm dữ liệu (403).";
                        if (response.StatusCode == HttpStatusCode.Conflict)
                            return "Tên đã tồn tại (409).";
                        if (!response.IsSuccessStatusCode)
                            return "API từ chối (HTTP " + (int)response.StatusCode + ").";
                        return "Đã thêm: " + name;
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is SerializationException)
            {
                return "Không thể gửi yêu cầu tới API.";
            }
        }

        private static string Encode<T>(T value)
        {
            var ser = new DataContractJsonSerializer(typeof(T));
            using (var stream = new MemoryStream())
            {
                ser.WriteObject(stream, value);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }
        private static T Decode<T>(string json)
        {
            var ser = new DataContractJsonSerializer(typeof(T));
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                return (T)ser.ReadObject(stream);
        }
        [DataContract]
        private sealed class ReferenceNewItem
        {
            [DataMember(Name = "name")] public string Name { get; set; }
            [DataMember(Name = "detail")] public string Detail { get; set; }
        }
    }

    [DataContract]
    public sealed class ReferenceEntry
    {
        [DataMember(Name = "id")] public long Id { get; set; }
        [DataMember(Name = "name")] public string Name { get; set; }
        [DataMember(Name = "detail")] public string Detail { get; set; }
    }
    [DataContract]
    public sealed class ReferenceListResult
    {
        [DataMember(Name = "authors")] public List<ReferenceEntry> Authors { get; set; }
        [DataMember(Name = "publishers")] public List<ReferenceEntry> Publishers { get; set; }
        [DataMember(Name = "categories")] public List<ReferenceEntry> Categories { get; set; }
        public bool IsSuccess { get; set; }
        public string Message { get; set; }
    }
}
