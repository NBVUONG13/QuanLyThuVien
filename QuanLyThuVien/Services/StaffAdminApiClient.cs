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
    public sealed class StaffAdminApiClient
    {
        private const string Url = "http://127.0.0.1:5082/api/admin/staff";
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        public async Task<StaffListResult> ListAsync(string token)
        {
            try
            {
                using (var req = Request(HttpMethod.Get, token))
                using (var resp = await Http.SendAsync(req))
                {
                    var body = await resp.Content.ReadAsStringAsync();
                    if (!resp.IsSuccessStatusCode) return new StaffListResult
                    {
                        Message = resp.StatusCode == HttpStatusCode.Forbidden ? "Không có quyền Admin." : "Không tải được danh sách. HTTP " + (int)resp.StatusCode
                    };
                    var data = Parse<StaffListResponse>(body);
                    return new StaffListResult { IsSuccess = true, Staff = data?.Staff ?? new List<StaffItem>() };
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is SerializationException)
            { return new StaffListResult { Message = "Không gọi được API quản lý nhân viên." }; }
        }

        public async Task<string> CreateAsync(string token, string username, string fullName, string staffCode, string password)
        {
            var model = new CreateRequest { Username = username, FullName = fullName, StaffCode = staffCode, Password = password };
            try
            {
                using (var req = Request(HttpMethod.Post, token))
                {
                    req.Content = new StringContent(Serialize(model), Encoding.UTF8, "application/json");
                    using (var resp = await Http.SendAsync(req))
                    {
                        string body = await resp.Content.ReadAsStringAsync();
                        if (resp.IsSuccessStatusCode) return "Đã tạo nhân viên Staff thành công.";
                        try { var error = Parse<ErrorResponse>(body); if (!string.IsNullOrWhiteSpace(error?.Message)) return error.Message; }
                        catch (SerializationException) { }
                        return "Không tạo được nhân viên (HTTP " + (int)resp.StatusCode + ").";
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
            { return "Không kết nối được API."; }
        }

        private static HttpRequestMessage Request(HttpMethod method, string token)
        {
            var req = new HttpRequestMessage(method, Url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return req;
        }
        private static string Serialize<T>(T value)
        {
            var s = new DataContractJsonSerializer(typeof(T));
            using (var m = new MemoryStream()) { s.WriteObject(m, value); return Encoding.UTF8.GetString(m.ToArray()); }
        }
        private static T Parse<T>(string json)
        {
            var s = new DataContractJsonSerializer(typeof(T));
            using (var m = new MemoryStream(Encoding.UTF8.GetBytes(json))) return (T)s.ReadObject(m);
        }
        [DataContract]
        private sealed class CreateRequest
        {
            [DataMember(Name = "username")] public string Username { get; set; }
            [DataMember(Name = "fullName")] public string FullName { get; set; }
            [DataMember(Name = "staffCode")] public string StaffCode { get; set; }
            [DataMember(Name = "password")] public string Password { get; set; }
        }
        [DataContract]
        private sealed class StaffListResponse
        {
            [DataMember(Name = "staff")] public List<StaffItem> Staff { get; set; }
        }
        [DataContract]
        private sealed class ErrorResponse
        {
            [DataMember(Name = "message")] public string Message { get; set; }
        }
    }
    public sealed class StaffListResult
    {
        public bool IsSuccess { get; set; }
        public string Message { get; set; }
        public List<StaffItem> Staff { get; set; } = new List<StaffItem>();
    }
    [DataContract]
    public sealed class StaffItem
    {
        [DataMember(Name = "userId")] public long UserId { get; set; }
        [DataMember(Name = "username")] public string Username { get; set; }
        [DataMember(Name = "staffCode")] public string StaffCode { get; set; }
        [DataMember(Name = "fullName")] public string FullName { get; set; }
        [DataMember(Name = "status")] public string Status { get; set; }
        [DataMember(Name = "role")] public string Role { get; set; }
    }
}
