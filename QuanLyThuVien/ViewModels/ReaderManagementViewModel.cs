using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows;
using System.Windows.Media;
using QuanLyThuVien.Services;

namespace QuanLyThuVien.ViewModels
{
    public sealed class ReaderManagementViewModel : MvvmBase
    {
        private readonly string _token;
        private readonly AuthApiClient _client = new AuthApiClient();
        private string _query = "", _message = "", _editFullName = "", _editPhone = "", _otp = "";
        private bool _busy, _otpPending;
        private Guid _pendingRequestId;
        private ReaderProfileItem _me;
        private ReaderProfileItem _selectedReader;
        private ReaderInsightResponse _insights;
        private bool _detailsLoaded, _restrictionBusy;
        private int _detailVersion;
        private string _restrictionReason = "", _decisionMessage = "";
        private bool _guestIdentityChecked, _guestClaimBusy;
        private string _guestClaimCode = "", _guestClaimMessage = "";
        public ReaderProfileItem SelectedReader
        {
            get => _selectedReader;
            set
            {
                if (!Set(ref _selectedReader, value)) return;
                ++_detailVersion;
                DetailsLoaded = false;
                Insights = null;
                GuestIdentityChecked = false;
                GuestClaimCode = "";
                GuestClaimMessage = "";
                LoanHistory.Clear(); RestrictionHistory.Clear(); MonthlyBorrowCounts.Clear();
                RefreshAccessCommands();
            }
        }
        public ReaderInsightResponse Insights { get => _insights; private set => Set(ref _insights, value); }
        public bool DetailsLoaded { get => _detailsLoaded; private set => Set(ref _detailsLoaded, value); }
        // Navigation is handled by MainViewModel; no modal visibility flag needed.
        public event EventHandler ReaderDetailRequested;
        public string RestrictionReason { get => _restrictionReason; set => Set(ref _restrictionReason, value); }
        public string DecisionMessage { get => _decisionMessage; private set => Set(ref _decisionMessage, value); }
        public ObservableCollection<ReaderLoanHistory> LoanHistory { get; } = new ObservableCollection<ReaderLoanHistory>();
        public ObservableCollection<ReaderAccessEvent> RestrictionHistory { get; } = new ObservableCollection<ReaderAccessEvent>();
        public ObservableCollection<ReaderMonthStat> MonthlyBorrowCounts { get; } = new ObservableCollection<ReaderMonthStat>();
        public ICommand OpenReaderDetailsCommand { get; }
        public ICommand RefreshDetailsCommand { get; }
        public ICommand BlockBorrowCommand { get; }
        public ICommand UnblockBorrowCommand { get; }
        public ICommand IssueGuestClaimCommand { get; }
        public bool GuestIdentityChecked
        {
            get => _guestIdentityChecked;
            set { if (Set(ref _guestIdentityChecked, value)) (IssueGuestClaimCommand as AsyncCommand)?.Refresh(); }
        }
        public string GuestClaimCode { get => _guestClaimCode; private set => Set(ref _guestClaimCode, value); }
        public string GuestClaimMessage { get => _guestClaimMessage; private set => Set(ref _guestClaimMessage, value); }
        public bool GuestClaimBusy
        {
            get => _guestClaimBusy;
            private set { if (Set(ref _guestClaimBusy, value)) (IssueGuestClaimCommand as AsyncCommand)?.Refresh(); }
        }
        private void RefreshAccessCommands()
        {
            (RefreshDetailsCommand as AsyncCommand)?.Refresh();
            (BlockBorrowCommand as AsyncCommand)?.Refresh();
            (UnblockBorrowCommand as AsyncCommand)?.Refresh();
            (IssueGuestClaimCommand as AsyncCommand)?.Refresh();
        }

        public ReaderManagementViewModel(string token, string role)
        {
            _token = token;
            IsReader = role == "Reader";
            CanManageReaders = role == "Staff" || role == "Admin";
            SearchCommand = new AsyncCommand(_ => LoadAsync(), _ => !Busy);
            StartProfileEditCommand = new AsyncCommand(_ => StartProfileEditAsync(), _ => IsReader && !Busy && !OtpPending);
            ConfirmProfileEditCommand = new AsyncCommand(_ => ConfirmProfileEditAsync(), _ => IsReader && !Busy && OtpPending);
            CancelProfileEditCommand = new ActionCommand(_ => CancelProfileEdit(), _ => IsReader && !Busy);
            // Works both from the selected-row action and the clearly visible toolbar button.
            // Keep CanExecute independent of the DataGrid row binding so the action never
            // appears mysteriously disabled while the Reader list is visible.
            OpenReaderDetailsCommand = new AsyncCommand(async parameter =>
            {
                if (!CanManageReaders) return;
                var reader = parameter as ReaderProfileItem ?? SelectedReader;
                if (reader == null)
                {
                    Message = "Hãy chọn một độc giả trong bảng trước khi xem chi tiết.";
                    return;
                }
                SelectedReader = reader;
                // Navigate BEFORE the API call. On failure the page remains visible,
                // showing SelectedReader and an explicit error message.
                DetailsLoaded = false;
                DecisionMessage = "Đang tải thống kê, lịch sử và quyền mượn...";
                ReaderDetailRequested?.Invoke(this, EventArgs.Empty);
                await LoadSelectedDetailsAsync();
            }, parameter => CanManageReaders);
            RefreshDetailsCommand = new AsyncCommand(_ => LoadSelectedDetailsAsync(), _ => CanManageReaders && SelectedReader != null && !_restrictionBusy);
            BlockBorrowCommand = new AsyncCommand(_ => UpdateBorrowAccessAsync(true),
                _ => CanManageReaders && DetailsLoaded && Insights?.Reader != null && !Insights.Reader.BorrowBlocked && !_restrictionBusy);
            UnblockBorrowCommand = new AsyncCommand(_ => UpdateBorrowAccessAsync(false),
                _ => CanManageReaders && DetailsLoaded && Insights?.Reader != null && Insights.Reader.BorrowBlocked && !_restrictionBusy);
            IssueGuestClaimCommand = new AsyncCommand(_ => IssueGuestClaimAsync(),
                _ => CanManageReaders && SelectedReader != null && SelectedReader.IsGuest &&
                     GuestIdentityChecked && !GuestClaimBusy && !Busy && string.IsNullOrEmpty(GuestClaimCode));
        }

        public bool IsReader { get; }
        public bool CanManageReaders { get; }
        public ObservableCollection<ReaderProfileItem> Readers { get; } =
            new ObservableCollection<ReaderProfileItem>();
        public ReaderProfileItem Me { get => _me; private set => Set(ref _me, value); }
        public string Query { get => _query; set => Set(ref _query, value); }
        public string Message { get => _message; private set => Set(ref _message, value); }
        public string EditFullName { get => _editFullName; set => Set(ref _editFullName, value); }
        public string EditPhone { get => _editPhone; set => Set(ref _editPhone, value); }
        public string Otp { get => _otp; set => Set(ref _otp, value); }
        public bool OtpPending
        {
            get => _otpPending;
            private set
            {
                if (!Set(ref _otpPending, value)) return;
                (StartProfileEditCommand as AsyncCommand)?.Refresh();
                (ConfirmProfileEditCommand as AsyncCommand)?.Refresh();
            }
        }
        public bool Busy
        {
            get => _busy;
            private set
            {
                if (!Set(ref _busy, value)) return;
                (SearchCommand as AsyncCommand)?.Refresh();
                (StartProfileEditCommand as AsyncCommand)?.Refresh();
                (ConfirmProfileEditCommand as AsyncCommand)?.Refresh();
                (CancelProfileEditCommand as ActionCommand)?.Refresh();
            }
        }

        public ICommand SearchCommand { get; }
        public ICommand StartProfileEditCommand { get; }
        public ICommand ConfirmProfileEditCommand { get; }
        public ICommand CancelProfileEditCommand { get; }

        public async Task LoadAsync()
        {
            if (Busy) return;
            Busy = true;
            Message = "Đang tải dữ liệu độc giả...";
            try
            {
                if (IsReader)
                {
                    var data = await _client.GetMyReaderProfileAsync(_token);
                    if (!data.IsSuccess || data.Reader == null)
                    {
                        Message = data.Message ?? "Không tìm thấy hồ sơ độc giả.";
                        return;
                    }
                    Me = data.Reader;
                    if (!OtpPending)
                    {
                        EditFullName = Me.FullName;
                        EditPhone = Me.Phone;
                    }
                    Message = "Hồ sơ cá nhân được lấy từ API.";
                    return;
                }
                if (!CanManageReaders) { Message = "Không có quyền xem danh sách."; return; }
                if ((Query ?? "").Length > 100) { Message = "Từ khóa quá dài (tối đa 100 ký tự)."; return; }
                var result = await _client.FindReadersAsync(_token, Query);
                if (!result.IsSuccess)
                {
                    Message = result.Message ?? "Không tải được độc giả.";
                    return;
                }
                Readers.Clear();
                if (result.Readers != null)
                    foreach (var reader in result.Readers) Readers.Add(reader);
                Message = Readers.Count == 0 ? "Không có độc giả phù hợp." :
                          "Tìm thấy " + Readers.Count + " độc giả (tối đa 100 kết quả).";
            }
            catch (Exception) { Message = "Không thể tải dữ liệu độc giả lúc này."; }
            finally { Busy = false; }
        }

        private async Task IssueGuestClaimAsync()
        {
            if (!CanManageReaders || SelectedReader == null || !SelectedReader.IsGuest ||
                !GuestIdentityChecked || GuestClaimBusy) return;
            long guestId = SelectedReader.UserId;
            GuestClaimBusy = true;
            GuestClaimCode = "";
            GuestClaimMessage = "Đang xin mã liên kết sử dụng một lần...";
            try
            {
                var result = await _client.IssueGuestClaimAsync(_token, guestId);
                if (SelectedReader?.UserId != guestId) return;
                if (!result.IsSuccess)
                { GuestClaimMessage = result.Message ?? "Không cấp được mã."; return; }
                GuestClaimCode = result.GuestClaimCode;
                GuestClaimMessage = "Chỉ trao trực tiếp cho đúng chủ hồ sơ. Mã hiệu lực 10 phút; cùng với mã Guest để đăng ký.";
            }
            catch (Exception) { GuestClaimMessage = "Không cấp được mã liên kết."; }
            finally { GuestClaimBusy = false; }
        }

        private async Task LoadSelectedDetailsAsync()
        {
            if (!CanManageReaders || SelectedReader == null) return;
            int version = _detailVersion;
            long readerId = SelectedReader.UserId;
            DecisionMessage = "Đang tải chi tiết và thống kê Reader...";
            try
            {
                var data = await _client.GetReaderInsightsAsync(_token, readerId);
                if (version != _detailVersion || SelectedReader?.UserId != readerId) return;
                if (!data.IsSuccess) { DecisionMessage = string.IsNullOrWhiteSpace(data.Message) ? "Không tải được thống kê. Hãy xem lỗi SQLSTATE trong terminal API." : data.Message; return; }
                Insights = data;
                LoanHistory.Clear();
                if (data.Loans != null) foreach (var x in data.Loans) LoanHistory.Add(x);
                RestrictionHistory.Clear();
                if (data.Restrictions != null) foreach (var x in data.Restrictions) RestrictionHistory.Add(x);
                MonthlyBorrowCounts.Clear();
                long max = 1;
                if (data.MonthlyBorrowCounts != null)
                    foreach (var x in data.MonthlyBorrowCounts) max = Math.Max(max, x.Count);
                if (data.MonthlyBorrowCounts != null)
                    foreach (var x in data.MonthlyBorrowCounts)
                    {
                        x.Percent = 100.0 * x.Count / max;
                        MonthlyBorrowCounts.Add(x);
                    }
                DetailsLoaded = true;
                DecisionMessage = "Đã tải chi tiết từ PostgreSQL. Số lần quá hạn gồm trả trễ và đang quá hạn.";
            }
            catch (Exception) { if (version == _detailVersion) DecisionMessage = "Không tải được chi tiết Reader."; }
            finally { RefreshAccessCommands(); }
        }
        private async Task UpdateBorrowAccessAsync(bool block)
        {
            if (!CanManageReaders || !DetailsLoaded || SelectedReader == null || _restrictionBusy) return;
            string reason = (RestrictionReason ?? "").Trim();
            if (reason.Length < 8 || reason.Length > 500)
            { DecisionMessage = "Cần nhập lý do từ 8 đến 500 ký tự."; return; }
            _restrictionBusy = true; RefreshAccessCommands();
            long readerId = SelectedReader.UserId;
            try
            {
                var result = await _client.ChangeReaderBorrowAccessAsync(_token, readerId, block, reason);
                DecisionMessage = result.Message;
                if (result.IsSuccess)
                {
                    RestrictionReason = "";
                    await LoadSelectedDetailsAsync();
                    DecisionMessage = result.Message;
                }
            }
            catch (Exception) { DecisionMessage = "Không đổi được quyền mượn của Reader."; }
            finally { _restrictionBusy = false; RefreshAccessCommands(); }
        }

        private async Task StartProfileEditAsync()
        {
            if (Busy || !IsReader || OtpPending) return;
            string name = (EditFullName ?? "").Trim();
            string phone = (EditPhone ?? "").Trim();
            if (name.Length < 2 || name.Length > 200)
            { Message = "Họ tên phải dài 2–200 ký tự."; return; }
            if (phone.Length > 24)
            { Message = "Số điện thoại quá dài."; return; }
            Busy = true;
            Message = "Đang yêu cầu mã OTP...";
            try
            {
                var result = await _client.StartProfileEditAsync(_token, name, phone);
                if (!result.IsSuccess)
                { Message = result.Message ?? "Không gửi được yêu cầu OTP."; return; }
                _pendingRequestId = result.RequestId;
                Otp = "";
                OtpPending = true;
                Message = "Mã OTP được in trong terminal API (demo localhost), không phải SMS. Có hiệu lực 5 phút.";
            }
            catch (Exception) { Message = "Không gửi được yêu cầu OTP."; }
            finally { Busy = false; }
        }

        private async Task ConfirmProfileEditAsync()
        {
            if (Busy || !IsReader || !OtpPending) return;
            if ((Otp ?? "").Length != 6 || !System.Text.RegularExpressions.Regex.IsMatch(Otp, @"\A\d{6}\z"))
            { Message = "OTP phải là đúng 6 chữ số."; return; }
            Busy = true;
            string code = Otp;
            Otp = "";
            Message = "Đang xác minh OTP và cập nhật hồ sơ...";
            try
            {
                var result = await _client.ConfirmProfileEditAsync(_token, _pendingRequestId, code);
                if (!result.IsSuccess)
                {
                    Message = result.Message ?? "OTP không hợp lệ.";
                    // Wrong code can be retried. Expired codes can be re-requested after reset.
                    if (Message.IndexOf("het han", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        Message.IndexOf("hết hạn", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        Message.IndexOf("Qua so lan", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        Message.IndexOf("Khong co yeu cau", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        Message.IndexOf("đã thay đổi", StringComparison.OrdinalIgnoreCase) >= 0)
                    { _pendingRequestId = Guid.Empty; OtpPending = false; }
                    return;
                }
                _pendingRequestId = Guid.Empty;
                OtpPending = false;
                var refreshed = await _client.GetMyReaderProfileAsync(_token);
                if (refreshed.IsSuccess && refreshed.Reader != null)
                {
                    Me = refreshed.Reader;
                    EditFullName = Me.FullName;
                    EditPhone = Me.Phone;
                    Message = "Đã xác nhận OTP và lưu họ tên / số điện thoại thành công.";
                }
                else Message = "Đã lưu thành công. Nhấn Hồ sơ của tôi để tải lại thông tin.";
            }
            catch (Exception) { Message = "Không thể xác nhận hồ sơ lúc này."; }
            finally { code = null; Busy = false; }
        }

        private void CancelProfileEdit()
        {
            // Local form reset; server OTP still expires after five minutes.
            _pendingRequestId = Guid.Empty;
            OtpPending = false;
            Otp = "";
            EditFullName = Me?.FullName ?? "";
            EditPhone = Me?.Phone ?? "";
            Message = "Đã khôi phục nội dung từ hồ sơ hiện tại.";
        }
    }

    public sealed class ReaderDetailPage
    {
        public ReaderDetailPage(ReaderManagementViewModel reader, ICommand backCommand)
        {
            Reader = reader ?? throw new ArgumentNullException(nameof(reader));
            BackCommand = backCommand ?? throw new ArgumentNullException(nameof(backCommand));
        }

        public ReaderManagementViewModel Reader { get; }
        public ICommand BackCommand { get; }
    }

}
