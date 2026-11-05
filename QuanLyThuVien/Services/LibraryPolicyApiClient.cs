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
    public sealed class LibraryPolicyApiClient
    {
        private static readonly string Url = global::QuanLyThuVien.App.ApiBaseUrl + "/api/admin/library-policy";
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };

        public async Task<LibraryPolicyResult> LoadAsync(string token)
        {
            try
            {
                using (var req = NewRequest(HttpMethod.Get, token))
                using (var resp = await Http.SendAsync(req))
                {
                    var body = await resp.Content.ReadAsStringAsync();
                    if (!resp.IsSuccessStatusCode)
                        return new LibraryPolicyResult { Message = GetError(body, resp.StatusCode) };
                    var data = Deserialize<LibraryPolicyResponse>(body);
                    return new LibraryPolicyResult
                    {
                        IsSuccess = true,
                        Current = data == null ? null : data.Current,
                        History = data == null || data.History == null ? new List<LibraryPolicyItem>() : data.History
                    };
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is SerializationException || ex is IOException)
            { return new LibraryPolicyResult { Message = "Không kết nối được API chính sách thư viện." }; }
        }

        public async Task<LibraryPolicySaveResult> SaveWithStatusAsync(string token, LibraryPolicyEdit value)
        {
            try
            {
                using (var req = NewRequest(HttpMethod.Post, token))
                {
                    req.Content = new StringContent(Serialize(value), Encoding.UTF8, "application/json");
                    using (var resp = await Http.SendAsync(req))
                    {
                        var body = await resp.Content.ReadAsStringAsync();
                        if (!resp.IsSuccessStatusCode) return new LibraryPolicySaveResult { Message = GetError(body, resp.StatusCode) };
                        var message = Deserialize<ServerMessage>(body);
                        return new LibraryPolicySaveResult
                        {
                            IsSuccess = true,
                            Message = message == null || string.IsNullOrWhiteSpace(message.Message)
                            ? "Đã lưu chính sách." : message.Message
                        };
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is SerializationException || ex is IOException)
            { return new LibraryPolicySaveResult { Message = "Không kết nối được API để lưu chính sách." }; }
        }

        private static HttpRequestMessage NewRequest(HttpMethod method, string token)
        {
            var req = new HttpRequestMessage(method, Url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return req;
        }
        private static string GetError(string json, HttpStatusCode status)
        {
            try
            {
                var msg = Deserialize<ServerMessage>(json);
                if (!string.IsNullOrWhiteSpace(msg == null ? null : msg.Message)) return msg.Message;
            }
            catch (SerializationException) { }
            if (status == HttpStatusCode.Unauthorized) return "Phiên đăng nhập đã hết hạn.";
            if (status == HttpStatusCode.Forbidden) return "Chỉ Admin được quyền thay đổi chính sách.";
            return "Lỗi API chính sách HTTP " + (int)status;
        }
        private static T Deserialize<T>(string value)
        {
            var json = new DataContractJsonSerializer(typeof(T));
            using (var mem = new MemoryStream(Encoding.UTF8.GetBytes(value)))
                return (T)json.ReadObject(mem);
        }
        private static string Serialize<T>(T value)
        {
            var json = new DataContractJsonSerializer(typeof(T));
            using (var mem = new MemoryStream())
            {
                json.WriteObject(mem, value);
                return Encoding.UTF8.GetString(mem.ToArray());
            }
        }
        [DataContract]
        private sealed class LibraryPolicyResponse
        {
            [DataMember(Name = "current")] public LibraryPolicyItem Current { get; set; }
            [DataMember(Name = "history")] public List<LibraryPolicyItem> History { get; set; }
        }
        [DataContract]
        private sealed class ServerMessage
        {
            [DataMember(Name = "message")] public string Message { get; set; }
        }
    }

    public sealed class LibraryPolicySaveResult
    {
        public bool IsSuccess { get; set; }
        public string Message { get; set; }
    }
    public sealed class LibraryPolicyResult
    {
        public bool IsSuccess { get; set; }
        public string Message { get; set; }
        public LibraryPolicyItem Current { get; set; }
        public List<LibraryPolicyItem> History { get; set; } = new List<LibraryPolicyItem>();
    }
    [DataContract]
    public sealed class LibraryPolicyItem
    {
        [DataMember(Name = "policyVersionId")] public long PolicyVersionId { get; set; }
        [DataMember(Name = "effectiveAt")] public string EffectiveAt { get; set; }
        [DataMember(Name = "createdAt")] public string CreatedAt { get; set; }
        [DataMember(Name = "maxRenewals")] public int MaxRenewals { get; set; }
        [DataMember(Name = "renewalDays")] public int RenewalDays { get; set; }
        [DataMember(Name = "allowOverdueRenewalRequests")] public bool AllowOverdueRenewalRequests { get; set; }
        [DataMember(Name = "allowOverdueRenewalApproval")] public bool AllowOverdueRenewalApproval { get; set; }
        [DataMember(Name = "overdueFeePerDay")] public decimal OverdueFeePerDay { get; set; }
        [DataMember(Name = "overdueGraceDays")] public int OverdueGraceDays { get; set; }
        [DataMember(Name = "maxOverdueFeePerDetail")] public decimal MaxOverdueFeePerDetail { get; set; }
        [DataMember(Name = "notes")] public string Notes { get; set; }
        public string VersionLabel => "#" + PolicyVersionId;
    }
    [DataContract]
    public sealed class LibraryPolicyEdit
    {
        [DataMember(Name = "expectedPolicyVersionId")] public long ExpectedPolicyVersionId { get; set; }
        [DataMember(Name = "maxRenewals")] public int MaxRenewals { get; set; }
        [DataMember(Name = "renewalDays")] public int RenewalDays { get; set; }
        [DataMember(Name = "allowOverdueRenewalRequests")] public bool AllowOverdueRenewalRequests { get; set; }
        [DataMember(Name = "allowOverdueRenewalApproval")] public bool AllowOverdueRenewalApproval { get; set; }
        [DataMember(Name = "overdueFeePerDay")] public decimal OverdueFeePerDay { get; set; }
        [DataMember(Name = "overdueGraceDays")] public int OverdueGraceDays { get; set; }
        [DataMember(Name = "maxOverdueFeePerDetail")] public decimal MaxOverdueFeePerDetail { get; set; }
        [DataMember(Name = "notes")] public string Notes { get; set; }
    }
}
