using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Input;
using QuanLyThuVien.Services;

namespace QuanLyThuVien.ViewModels
{
    // One existing HR page reused for Staff self-attendance and Admin staff administration.
    public sealed class HRManagementViewModel : MvvmBase
    {
        private readonly StaffAdminApiClient _api = new StaffAdminApiClient();
        private readonly string _token;
        private string _username = "", _name = "", _code = "", _status = "";
        private string _attendanceStatus = "", _month;
        private decimal _totalHours;
        private int _openCount;
        private bool _busy;
        private PayrollRow _selectedPayroll;
        private string _payrollStatus = "", _newHourlyRate = "", _rateReason = "";
        private string _payrollLinesStatus = "Chọn nhân viên trong bảng lương phía trên để xem các ca.";
        private int _payrollLinesRequestVersion;

        public HRManagementViewModel(string token, string role)
        {
            _token = token;
            IsAdmin = string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase);
            IsStaff = string.Equals(role, "Staff", StringComparison.OrdinalIgnoreCase);
            _month = DateTime.UtcNow.AddHours(7).ToString("yyyy-MM", CultureInfo.InvariantCulture);
            RefreshCommand = new AsyncCommand(_ => LoadAsync());
            CreateCommand = new AsyncCommand(p => CreateAsync(p as string));
            CheckInCommand = new AsyncCommand(_ => CheckAsync(true));
            CheckOutCommand = new AsyncCommand(_ => CheckAsync(false));
            PreparePayrollCommand = new AsyncCommand(_ => PreparePayrollAsync());
            FinalizePayrollCommand = new AsyncCommand(_ => UpdatePayrollStatusAsync(false));
            MarkPaidCommand = new AsyncCommand(_ => UpdatePayrollStatusAsync(true));
            UpdateRateCommand = new AsyncCommand(_ => UpdateRateAsync());
            LoadPayrollLinesCommand = new AsyncCommand(_ => LoadSelectedPayrollLinesAsync());
        }
        public bool IsAdmin { get; }
        public bool IsStaff { get; }
        public ObservableCollection<StaffItem> Staff { get; } = new ObservableCollection<StaffItem>();
        public ObservableCollection<AttendanceShift> Shifts { get; } = new ObservableCollection<AttendanceShift>();
        public ObservableCollection<PayrollRow> PayrollRows { get; } = new ObservableCollection<PayrollRow>();
        public ObservableCollection<PayrollLine> PayrollLines { get; } = new ObservableCollection<PayrollLine>();
        public PayrollRow SelectedPayroll
        {
            get { return _selectedPayroll; }
            set
            {
                if (!Set(ref _selectedPayroll, value)) return;

                NewHourlyRate = value == null ? "" :
                    value.CurrentHourlyRate.ToString("0.##", CultureInfo.InvariantCulture);
                PayrollLines.Clear();
                int requestVersion = ++_payrollLinesRequestVersion;

                if (value == null)
                {
                    PayrollLinesStatus = "Chọn nhân viên trong bảng lương phía trên để xem các ca.";
                }
                else if (value.SalaryId <= 0)
                {
                    PayrollLinesStatus = ExplainNotPrepared(value);
                }
                else
                {
                    PayrollLinesStatus = "Đang tải chi tiết các ca của " + value.FullName + "...";
                    // The selection triggers the same API call as the old manual button.
                    // Do not block on Busy: refresh/prepare is allowed to update selection.
                    _ = LoadPayrollLinesAsync(value, requestVersion);
                }
            }
        }
        public string PayrollLinesStatus { get => _payrollLinesStatus; private set => Set(ref _payrollLinesStatus, value); }
        public string NewHourlyRate { get => _newHourlyRate; set => Set(ref _newHourlyRate, value); }
        public string RateReason { get => _rateReason; set => Set(ref _rateReason, value); }
        public string PayrollStatus { get => _payrollStatus; private set => Set(ref _payrollStatus, value); }
        public string Username { get => _username; set => Set(ref _username, value); }
        public string FullName { get => _name; set => Set(ref _name, value); }
        public string StaffCode { get => _code; set => Set(ref _code, value); }
        public string Status { get => _status; private set => Set(ref _status, value); }
        public string AttendanceStatus { get => _attendanceStatus; private set => Set(ref _attendanceStatus, value); }
        public string Month { get => _month; set => Set(ref _month, value); }
        public decimal TotalHours { get => _totalHours; private set => Set(ref _totalHours, value); }
        public int OpenCount { get => _openCount; private set => Set(ref _openCount, value); }
        public bool Busy { get => _busy; private set => Set(ref _busy, value); }
        public ICommand RefreshCommand { get; }
        public ICommand CreateCommand { get; }
        public ICommand CheckInCommand { get; }
        public ICommand CheckOutCommand { get; }
        public ICommand PreparePayrollCommand { get; }
        public ICommand FinalizePayrollCommand { get; }
        public ICommand MarkPaidCommand { get; }
        public ICommand UpdateRateCommand { get; }
        public ICommand LoadPayrollLinesCommand { get; }

        public async Task LoadAsync()
        {
            if (Busy) return;
            Busy = true;
            try
            {
                if (IsAdmin)
                {
                    Status = "Đang tải danh sách nhân viên...";
                    var list = await _api.ListAsync(_token);
                    if (list.IsSuccess)
                    {
                        ReplaceStaff(list.Staff);
                        Status = "Đã tải " + Staff.Count + " nhân viên.";
                    }
                    else Status = list.Message;
                }
                await ReloadAttendanceAsync();
                if (IsAdmin) await ReloadPayrollAsync();
            }
            finally { Busy = false; }
        }
        private bool ValidMonth()
        {
            DateTime parsed;
            return DateTime.TryParseExact((Month ?? "").Trim() + "-01", "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed) &&
                parsed.Year >= 2000 && parsed.Year <= 2100;
        }
        private async Task ReloadPayrollAsync()
        {
            if (!IsAdmin) return;
            if (!ValidMonth()) { PayrollStatus = "Nhập tháng đúng dạng YYYY-MM."; return; }
            var selectedUserId = SelectedPayroll == null ? 0 : SelectedPayroll.StaffUserId;
            var result = await _api.ListPayrollAsync(_token, Month.Trim());
            if (!result.IsSuccess)
            {
                PayrollRows.Clear();
                SelectedPayroll = null;
                PayrollStatus = result.Message;
                return;
            }
            PayrollRows.Clear();
            PayrollRow restored = null;
            foreach (var row in result.Rows)
            {
                PayrollRows.Add(row);
                if (row.StaffUserId == selectedUserId) restored = row;
            }
            // Auto-select first employee on initial load, and keep the current employee on refresh.
            SelectedPayroll = restored ?? (PayrollRows.Count > 0 ? PayrollRows[0] : null);
            PayrollStatus = "Bảng lương tháng " + Month + ": " + PayrollRows.Count + " nhân viên.";
        }
        private async Task PreparePayrollAsync()
        {
            if (!IsAdmin || Busy) return;
            if (!ValidMonth()) { PayrollStatus = "Nhập tháng đúng dạng YYYY-MM."; return; }
            Busy = true;
            try
            {
                var message = await _api.PreparePayrollAsync(_token, Month.Trim());
                await ReloadPayrollAsync();
                PayrollStatus = message;
            }
            catch (Exception) { PayrollStatus = "Không tính được bảng lương. Kiểm tra API."; }
            finally { Busy = false; }
        }
        private async Task UpdatePayrollStatusAsync(bool paid)
        {
            if (!IsAdmin || Busy || SelectedPayroll == null) return;
            var item = SelectedPayroll;
            if (item.SalaryId <= 0) { PayrollStatus = "Hãy tính bảng lương trước."; return; }
            if (paid && item.Status != "finalized") { PayrollStatus = "Chỉ ghi nhận đã trả cho bảng lương đã chốt."; return; }
            if (!paid && item.Status != "draft") { PayrollStatus = "Chỉ chốt bảng lương còn nháp."; return; }
            Busy = true;
            try
            {
                var message = paid
                    ? await _api.MarkPayrollPaidAsync(_token, item.SalaryId)
                    : await _api.FinalizePayrollAsync(_token, item.SalaryId);
                await ReloadPayrollAsync();
                PayrollStatus = message;
            }
            catch (Exception) { PayrollStatus = "Không cập nhật được trạng thái bảng lương."; }
            finally { Busy = false; }
        }
        private async Task UpdateRateAsync()
        {
            if (!IsAdmin || Busy || SelectedPayroll == null) return;
            decimal rate;
            if (!decimal.TryParse((NewHourlyRate ?? "").Trim(), NumberStyles.Number,
                    CultureInfo.CurrentCulture, out rate) &&
                !decimal.TryParse((NewHourlyRate ?? "").Trim(), NumberStyles.Number,
                    CultureInfo.InvariantCulture, out rate))
            { PayrollStatus = "Đơn giá phải là số hợp lệ."; return; }
            if (rate <= 0 || rate > 10000000m || (RateReason ?? "").Trim().Length < 8)
            { PayrollStatus = "Đơn giá 1–10.000.000 VND/giờ; lý do ít nhất 8 ký tự."; return; }
            Busy = true;
            try
            {
                var msg = await _api.UpdateHourlyRateAsync(_token, SelectedPayroll.StaffUserId,
                    rate, RateReason.Trim());
                await ReloadPayrollAsync();
                PayrollStatus = msg;
            }
            catch (Exception) { PayrollStatus = "Không cập nhật được đơn giá giờ công."; }
            finally { Busy = false; }
        }
        // Kept the old manual Refresh button. Selection also loads automatically.
        private async Task LoadSelectedPayrollLinesAsync()
        {
            if (!IsAdmin) return;
            var selected = SelectedPayroll;
            int requestVersion = ++_payrollLinesRequestVersion;
            PayrollLines.Clear();
            if (selected == null)
            {
                PayrollLinesStatus = "Hãy chọn nhân viên ở bảng lương phía trên.";
                return;
            }
            if (selected.SalaryId <= 0)
            {
                PayrollLinesStatus = ExplainNotPrepared(selected);
                return;
            }
            PayrollLinesStatus = "Đang tải lại các ca của " + selected.FullName + "...";
            await LoadPayrollLinesAsync(selected, requestVersion);
        }

        private async Task LoadPayrollLinesAsync(PayrollRow selected, int requestVersion)
        {
            try
            {
                var result = await _api.ListPayrollLinesAsync(_token, selected.SalaryId);
                // Ignore an old response if the Admin selected a different employee meanwhile.
                if (requestVersion != _payrollLinesRequestVersion ||
                    !Object.ReferenceEquals(selected, SelectedPayroll)) return;
                if (!result.IsSuccess)
                {
                    PayrollLinesStatus = "Không tải được chi tiết ca: " + (result.Message ?? "Lỗi API.");
                    return;
                }
                PayrollLines.Clear();
                foreach (var item in result.Lines) PayrollLines.Add(item);
                if (PayrollLines.Count > 0)
                    PayrollLinesStatus = "Đã tải " + PayrollLines.Count + " ca của " + selected.FullName +
                        ". Đơn giá là mức đã lưu lúc check-in.";
                else if (selected.ClosedShifts == 0)
                    PayrollLinesStatus = selected.OpenShifts > 0
                        ? "Nhân viên đang có " + selected.OpenShifts + " ca chưa check-out. Ca đang mở chưa được tính lương."
                        : "Không có ca nào đã check-out trong tháng " + Month + " cho " + selected.FullName + ".";
                else
                    PayrollLinesStatus = "Chưa có dòng chi tiết dù có " + selected.ClosedShifts +
                        " ca đã check-out. Nếu bảng còn NHÁP, bấm TÍNH / CẬP NHẬT BẢNG NHÁP rồi kiểm tra lại.";
            }
            catch (Exception)
            {
                if (requestVersion == _payrollLinesRequestVersion &&
                    Object.ReferenceEquals(selected, SelectedPayroll))
                    PayrollLinesStatus = "Không tải được chi tiết ca. Kiểm tra API đang chạy và thử TẢI LẠI CHI TIẾT.";
            }
        }

        private string ExplainNotPrepared(PayrollRow selected)
        {
            if (selected.ClosedShifts > 0)
                return "Nhân viên " + selected.FullName + " có " + selected.ClosedShifts +
                    " ca đã check-out trong tháng " + Month +
                    ", nhưng CHƯA TÍNH BẢNG LƯƠNG. Bấm TÍNH / CẬP NHẬT BẢNG NHÁP ở phía trên.";
            if (selected.OpenShifts > 0)
                return "Nhân viên " + selected.FullName + " có " + selected.OpenShifts +
                    " ca đang mở. Staff cần check-out; sau đó Admin bấm TÍNH / CẬP NHẬT BẢNG NHÁP.";
            return "Nhân viên " + selected.FullName + " chưa có ca đã check-out trong tháng " +
                Month + ". Staff phải check-in/check-out trước khi có dữ liệu lương.";
        }
        private void ReplaceStaff(IEnumerable<StaffItem> data)
        {
            Staff.Clear();
            if (data != null) foreach (var item in data) Staff.Add(item);
        }
        private async Task ReloadAttendanceAsync()
        {
            DateTime test;
            if (!DateTime.TryParseExact((Month ?? "").Trim() + "-01", "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out test))
            { AttendanceStatus = "Nhập tháng đúng định dạng YYYY-MM, ví dụ 2026-11."; return; }
            AttendanceStatus = "Đang tải chấm công...";
            var result = await _api.ListAttendanceAsync(_token, Month.Trim());
            if (!result.IsSuccess) { AttendanceStatus = result.Message; return; }
            Shifts.Clear();
            foreach (var item in result.Shifts) Shifts.Add(item);
            TotalHours = result.TotalHours;
            OpenCount = result.OpenCount;
            AttendanceStatus = "Đã tải " + Shifts.Count + " ca. Giờ tính cho ca đã kết thúc.";
        }
        private async Task CheckAsync(bool checkIn)
        {
            if (!IsStaff || Busy) return;
            Busy = true;
            try
            {
                var actionMessage = await _api.AttendanceActionAsync(_token, checkIn);
                // After check-in/out, show current month so a shift is not hidden by a previous-month filter.
                Month = DateTime.UtcNow.AddHours(7).ToString("yyyy-MM", CultureInfo.InvariantCulture);
                await ReloadAttendanceAsync();
                AttendanceStatus = actionMessage + " " + AttendanceStatus;
            }
            finally { Busy = false; }
        }
        private async Task CreateAsync(string password)
        {
            if (!IsAdmin || Busy) return;
            if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(FullName) ||
                string.IsNullOrWhiteSpace(StaffCode) || string.IsNullOrWhiteSpace(password))
            { Status = "Nhập đủ username, họ tên, mã nhân viên và mật khẩu."; return; }
            Busy = true; Status = "Đang tạo Staff...";
            try
            {
                var result = await _api.CreateAsync(_token, Username.Trim(), FullName.Trim(), StaffCode.Trim(), password);
                Status = result;
                if (result == "Đã tạo nhân viên Staff thành công.")
                {
                    var list = await _api.ListAsync(_token);
                    if (list.IsSuccess)
                    {
                        ReplaceStaff(list.Staff);
                        Username = ""; FullName = ""; StaffCode = "";
                        Status = result + " Đã đồng bộ " + Staff.Count + " tài khoản.";
                    }
                    else Status = result + " Nhưng không tải lại được danh sách: " + list.Message;
                }
            }
            catch (Exception) { Status = "Không tạo được Staff. Kiểm tra API."; }
            finally { Busy = false; }
        }
    }
}
