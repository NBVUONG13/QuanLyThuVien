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

        // Step 79: Staff sees own shifts; Admin sees all shifts. Server enforces this.
        private const string AttendanceUrl = "http://127.0.0.1:5082/api/attendance";

        public async Task<AttendanceListResult> ListAttendanceAsync(string token, string month)
        {
            try
            {
                var url = AttendanceUrl + "/shifts?month=" + Uri.EscapeDataString(month ?? "");
                using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    using (var resp = await Http.SendAsync(req))
                    {
                        var body = await resp.Content.ReadAsStringAsync();
                        if (!resp.IsSuccessStatusCode)
                            return new AttendanceListResult { Message = AttendanceError(body, (int)resp.StatusCode) };
                        var result = Parse<AttendanceListResponse>(body);
                        return new AttendanceListResult
                        {
                            IsSuccess = true,
                            Shifts = result.Shifts ?? new List<AttendanceShift>(),
                            TotalHours = result.TotalHours,
                            OpenCount = result.OpenCount,
                            Month = result.Month
                        };
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is SerializationException)
            { return new AttendanceListResult { Message = "Không kết nối được API chấm công." }; }
        }

        public async Task<string> AttendanceActionAsync(string token, bool checkIn)
        {
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Post,
                    AttendanceUrl + (checkIn ? "/check-in" : "/check-out")))
                {
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    using (var resp = await Http.SendAsync(req))
                    {
                        var body = await resp.Content.ReadAsStringAsync();
                        if (!resp.IsSuccessStatusCode)
                            return AttendanceError(body, (int)resp.StatusCode);
                        var result = Parse<AttendanceActionResponse>(body);
                        return result.Message ?? "Đã ghi nhận chấm công.";
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is SerializationException)
            { return "Không kết nối được API chấm công."; }
        }

        private static string AttendanceError(string response, int status)
        {
            try
            {
                var error = Parse<ErrorResponse>(response);
                if (!string.IsNullOrWhiteSpace(error?.Message)) return error.Message;
            }
            catch (Exception ex) when (ex is SerializationException || ex is ArgumentException) { }
            if (status == 401) return "Phiên đăng nhập hết hạn. Hãy đăng nhập lại.";
            if (status == 403) return "Tài khoản không có quyền thực hiện.";
            return "Lỗi chấm công HTTP " + status + ".";
        }

        [DataContract]
        private sealed class AttendanceActionResponse
        {
            [DataMember(Name = "message")] public string Message { get; set; }
        }
        [DataContract]
        private sealed class AttendanceListResponse
        {
            [DataMember(Name = "month")] public string Month { get; set; }
            [DataMember(Name = "shifts")] public List<AttendanceShift> Shifts { get; set; }
            [DataMember(Name = "totalHours")] public decimal TotalHours { get; set; }
            [DataMember(Name = "openCount")] public int OpenCount { get; set; }
        }


        // Step 80: Admin-only payroll. Existing Staff and Attendance methods are unchanged.
        private const string PayrollUrl = "http://127.0.0.1:5082/api/admin/payroll";

        public async Task<PayrollListResult> ListPayrollAsync(string token, string month)
        {
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get,
                    PayrollUrl + "?month=" + Uri.EscapeDataString(month ?? "")))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    using (var response = await Http.SendAsync(request))
                    {
                        var json = await response.Content.ReadAsStringAsync();
                        if (!response.IsSuccessStatusCode)
                            return new PayrollListResult { Message = AttendanceError(json, (int)response.StatusCode) };
                        var data = Parse<PayrollListResponse>(json);
                        return new PayrollListResult { IsSuccess = true, Rows = data.Rows ?? new List<PayrollRow>() };
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is SerializationException)
            { return new PayrollListResult { Message = "Không kết nối được API bảng lương." }; }
        }

        public async Task<PayrollLinesResult> ListPayrollLinesAsync(string token, long salaryId)
        {
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, PayrollUrl + "/" + salaryId + "/lines"))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    using (var response = await Http.SendAsync(request))
                    {
                        var json = await response.Content.ReadAsStringAsync();
                        if (!response.IsSuccessStatusCode)
                            return new PayrollLinesResult { Message = AttendanceError(json, (int)response.StatusCode) };
                        var data = Parse<PayrollLinesResponse>(json);
                        return new PayrollLinesResult { IsSuccess = true, Lines = data.Lines ?? new List<PayrollLine>() };
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is SerializationException)
            { return new PayrollLinesResult { Message = "Không tải được các ca trong bảng lương." }; }
        }

        public Task<string> PreparePayrollAsync(string token, string month)
        {
            return PayrollPostAsync(token, PayrollUrl + "/prepare", new MonthRequest { Month = month });
        }
        public Task<string> FinalizePayrollAsync(string token, long salaryId)
        {
            return PayrollPostAsync(token, PayrollUrl + "/" + salaryId + "/finalize", null);
        }
        public Task<string> MarkPayrollPaidAsync(string token, long salaryId)
        {
            return PayrollPostAsync(token, PayrollUrl + "/" + salaryId + "/paid", null);
        }
        public Task<string> UpdateHourlyRateAsync(string token, long staffUserId, decimal rate, string reason)
        {
            return PayrollPostAsync(token, PayrollUrl + "/staff/" + staffUserId + "/rate",
                new RateRequest { HourlyRate = rate, Reason = reason });
        }
        private async Task<string> PayrollPostAsync(string token, string url, object body)
        {
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    if (body != null)
                    {
                        string json = body is MonthRequest ? Serialize((MonthRequest)body) : Serialize((RateRequest)body);
                        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                    }
                    using (var response = await Http.SendAsync(request))
                    {
                        var json = await response.Content.ReadAsStringAsync();
                        if (!response.IsSuccessStatusCode)
                            return AttendanceError(json, (int)response.StatusCode);
                        var data = Parse<ErrorResponse>(json);
                        return !string.IsNullOrWhiteSpace(data?.Message) ? data.Message : "Thao tác thành công.";
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is SerializationException)
            { return "Không kết nối được API hoặc không đọc được phản hồi bảng lương."; }
        }
        [DataContract]
        private sealed class MonthRequest
        { [DataMember(Name = "month")] public string Month { get; set; } }
        [DataContract]
        private sealed class RateRequest
        {
            [DataMember(Name = "hourlyRate")] public decimal HourlyRate { get; set; }
            [DataMember(Name = "reason")] public string Reason { get; set; }
        }
        [DataContract]
        private sealed class PayrollListResponse
        { [DataMember(Name = "rows")] public List<PayrollRow> Rows { get; set; } }
        [DataContract]
        private sealed class PayrollLinesResponse
        { [DataMember(Name = "lines")] public List<PayrollLine> Lines { get; set; } }

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

namespace QuanLyThuVien.Services
{
    public sealed class AttendanceListResult
    {
        public bool IsSuccess { get; set; }
        public string Message { get; set; }
        public string Month { get; set; }
        public decimal TotalHours { get; set; }
        public int OpenCount { get; set; }
        public List<AttendanceShift> Shifts { get; set; } = new List<AttendanceShift>();
    }

    [DataContract]
    public sealed class AttendanceShift
    {
        [DataMember(Name = "timesheetId")] public long TimesheetId { get; set; }
        [DataMember(Name = "staffUserId")] public long StaffUserId { get; set; }
        [DataMember(Name = "staffCode")] public string StaffCode { get; set; }
        [DataMember(Name = "fullName")] public string FullName { get; set; }
        [DataMember(Name = "checkIn")] public string CheckIn { get; set; }
        [DataMember(Name = "checkOut")] public string CheckOut { get; set; }
        [DataMember(Name = "isOpen")] public bool IsOpen { get; set; }
        [DataMember(Name = "hours")] public decimal Hours { get; set; }
        [DataMember(Name = "hourlyRate")] public decimal HourlyRate { get; set; }
        public string ShiftStatus { get { return IsOpen ? "ĐANG LÀM" : "ĐÃ KẾT THÚC"; } }
    }
}


namespace QuanLyThuVien.Services
{
    public sealed class PayrollListResult
    {
        public bool IsSuccess { get; set; }
        public string Message { get; set; }
        public List<PayrollRow> Rows { get; set; } = new List<PayrollRow>();
    }
    public sealed class PayrollLinesResult
    {
        public bool IsSuccess { get; set; }
        public string Message { get; set; }
        public List<PayrollLine> Lines { get; set; } = new List<PayrollLine>();
    }
    [DataContract]
    public sealed class PayrollRow
    {
        [DataMember(Name = "staffUserId")] public long StaffUserId { get; set; }
        [DataMember(Name = "staffCode")] public string StaffCode { get; set; }
        [DataMember(Name = "fullName")] public string FullName { get; set; }
        [DataMember(Name = "currentHourlyRate")] public decimal CurrentHourlyRate { get; set; }
        [DataMember(Name = "salaryId")] public long SalaryId { get; set; }
        [DataMember(Name = "status")] public string Status { get; set; }
        [DataMember(Name = "totalHours")] public decimal TotalHours { get; set; }
        [DataMember(Name = "totalAmount")] public decimal TotalAmount { get; set; }
        [DataMember(Name = "closedShifts")] public long ClosedShifts { get; set; }
        [DataMember(Name = "openShifts")] public long OpenShifts { get; set; }
        public string StatusText
        {
            get
            {
                switch (Status)
                {
                    case "draft": return "NHÁP";
                    case "finalized": return "ĐÃ CHỐT";
                    case "paid": return "ĐÃ TRẢ";
                    default: return "CHƯA TÍNH";
                }
            }
        }
    }
    [DataContract]
    public sealed class PayrollLine
    {
        [DataMember(Name = "timesheetId")] public long TimesheetId { get; set; }
        [DataMember(Name = "checkIn")] public string CheckIn { get; set; }
        [DataMember(Name = "checkOut")] public string CheckOut { get; set; }
        [DataMember(Name = "hours")] public decimal Hours { get; set; }
        [DataMember(Name = "hourlyRate")] public decimal HourlyRate { get; set; }
        [DataMember(Name = "amount")] public decimal Amount { get; set; }
    }
}
