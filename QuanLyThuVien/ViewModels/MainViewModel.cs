using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using QuanLyThuVien.Services;

namespace QuanLyThuVien.ViewModels
{
    // Single page for browsing/searching/filtering books. Statistics are a separate, genuinely different page.
    public sealed class MainViewModel : MvvmBase
    {
        private readonly string _token;
        private readonly AuthApiClient _auth = new AuthApiClient();
        private object _currentPage;
        private string _username = "", _role = "", _status = "Đang xác minh phiên...";
        private bool _authenticated, _loggingOut;

        public event EventHandler LogoutRequested;

        public MainViewModel(string token)
        {
            _token = token;
            Dashboard = new DashboardViewModel(token);
            HomeCommand = new AsyncCommand(_ => NavigateHomeAsync());
            DashboardCommand = new AsyncCommand(_ => NavigateDashboardAsync());
            StaffCommand = new AsyncCommand(_ => NavigateStaffAsync(), _ => IsAdmin);
            ReaderCommand = new AsyncCommand(_ => NavigateReadersAsync(), _ => IsAuthenticated);
            LoansCommand = new AsyncCommand(_ => NavigateLoansAsync(), _ => IsAuthenticated);
            LogoutCommand = new AsyncCommand(_ => LogoutAsync());
            PendingCommand = new ActionCommand(p =>
            {
                if (IsAuthenticated)
                    CurrentPage = "Chức năng '" + p + "' đang được phát triển.";
            });
        }

        public DashboardViewModel Dashboard { get; }
        public BookManagementViewModel Books { get; private set; }
        public HRManagementViewModel Staff { get; private set; }
        public ReaderManagementViewModel ReaderPanel { get; private set; }
        public BorrowReturnViewModel Loans { get; private set; }
        public object CurrentPage { get => _currentPage; private set => Set(ref _currentPage, value); }
        public string Username { get => _username; private set => Set(ref _username, value); }
        public string Role { get => _role; private set => Set(ref _role, value); }
        public string Status { get => _status; private set => Set(ref _status, value); }
        public bool IsAuthenticated { get => _authenticated; private set => Set(ref _authenticated, value); }
        public bool IsReader => IsAuthenticated && Role == "Reader";
        public bool IsAdmin => IsAuthenticated && Role == "Admin";
        public bool CanManage => IsAuthenticated && (Role == "Staff" || Role == "Admin");

        public ICommand HomeCommand { get; }
        public ICommand DashboardCommand { get; }
        public ICommand StaffCommand { get; }
        public ICommand ReaderCommand { get; }
        public ICommand LoansCommand { get; }
        public ICommand LogoutCommand { get; }
        public ICommand PendingCommand { get; }

        public async Task InitializeAsync()
        {
            var result = await _auth.ValidateAsync(_token);
            if (!result.IsSuccess ||
                (result.Role != "Admin" && result.Role != "Staff" && result.Role != "Reader"))
            {
                Status = "Phiên đăng nhập không hợp lệ. Vui lòng đăng nhập lại.";
                LogoutRequested?.Invoke(this, EventArgs.Empty);
                return;
            }

            Username = result.Username;
            Role = result.Role;
            IsAuthenticated = true;
            Notify(nameof(IsReader));
            Notify(nameof(IsAdmin));
            Notify(nameof(CanManage));
            (StaffCommand as AsyncCommand)?.Refresh();
            (ReaderCommand as AsyncCommand)?.Refresh();
            (LoansCommand as AsyncCommand)?.Refresh();

            // Reuse one BookManagementViewModel for all browsing/search/metadata/copies work.
            Books = new BookManagementViewModel(_token, Role);
            if (IsAdmin) Staff = new HRManagementViewModel(_token);
            ReaderPanel = new ReaderManagementViewModel(_token, Role);
            Loans = new BorrowReturnViewModel(_token, Role);
            Status = "Đã xác thực phiên: " + Username + " (" + Role + ")";

            // Open the bookstore-style home page immediately after login.
            await NavigateHomeAsync();
        }

        private async Task NavigateHomeAsync()
        {
            if (!IsAuthenticated || Books == null) return;
            CurrentPage = Books;
            await Books.InitializeAsync();
        }

        private async Task NavigateDashboardAsync()
        {
            if (!IsAuthenticated) return;
            CurrentPage = Dashboard;
            await Dashboard.LoadAsync();
        }

        private async Task NavigateStaffAsync()
        {
            if (!IsAdmin || Staff == null) return;
            CurrentPage = Staff;
            await Staff.LoadAsync();
        }

        private async Task NavigateReadersAsync()
        {
            if (!IsAuthenticated || ReaderPanel == null) return;
            CurrentPage = ReaderPanel;
            await ReaderPanel.LoadAsync();
        }

        private async Task NavigateLoansAsync()
        {
            if (!IsAuthenticated || Loans == null) return;
            CurrentPage = Loans;
            await Loans.LoadAsync();
        }

        private async Task LogoutAsync()
        {
            if (_loggingOut) return;
            _loggingOut = true;
            try { await _auth.LogoutAsync(_token); }
            finally { LogoutRequested?.Invoke(this, EventArgs.Empty); }
        }
    }

    // Reader view-model stays in the existing MainViewModel.cs (no redundant class/file).
    // ObservableCollection is for the Staff/Admin list, and properties use INotifyPropertyChanged.
    public sealed class ReaderManagementViewModel : MvvmBase
    {
        private readonly string _token;
        private readonly AuthApiClient _client = new AuthApiClient();
        private string _query = "", _message = "", _editFullName = "", _editPhone = "", _otp = "";
        private bool _busy, _otpPending;
        private Guid _pendingRequestId;
        private ReaderProfileItem _me;

        public ReaderManagementViewModel(string token, string role)
        {
            _token = token;
            IsReader = role == "Reader";
            CanManageReaders = role == "Staff" || role == "Admin";
            SearchCommand = new AsyncCommand(_ => LoadAsync(), _ => !Busy);
            StartProfileEditCommand = new AsyncCommand(_ => StartProfileEditAsync(), _ => IsReader && !Busy && !OtpPending);
            ConfirmProfileEditCommand = new AsyncCommand(_ => ConfirmProfileEditAsync(), _ => IsReader && !Busy && OtpPending);
            CancelProfileEditCommand = new ActionCommand(_ => CancelProfileEdit(), _ => IsReader && !Busy);
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



    // Step54: lending domain lives in a dedicated VM TYPE, in the existing MainViewModel.cs
    // so the user does not need to add an otherwise empty/duplicate screen or update csproj.
    public sealed class BorrowReturnViewModel : MvvmBase
    {
        private readonly string _token;
        private readonly LoanApiClient _api = new LoanApiClient();
        private string _message = "", _readerUsername = "", _copyCode = "", _loanDays = "14", _copyQuery = "";
        private bool _busy;
        private LoanItem _selectedLoan;
        private AvailableLoanCopy _selectedCopy;

        public BorrowReturnViewModel(string token, string role)
        {
            _token = token;
            CanManage = role == "Staff" || role == "Admin";
            IsReader = role == "Reader";
            RefreshCommand = new AsyncCommand(_ => LoadAsync(), _ => !Busy);
            SearchAvailableCommand = new AsyncCommand(_ => SearchCopiesAsync(), _ => CanManage && !Busy);
            CheckoutCommand = new AsyncCommand(_ => CheckoutAsync(), _ => CanManage && !Busy);
            ReturnCommand = new AsyncCommand(_ => ReturnAsync(),
                _ => CanManage && !Busy && SelectedLoan != null && SelectedLoan.IsOpen);
        }
        public bool CanManage { get; }
        public bool IsReader { get; }
        public ObservableCollection<LoanItem> Loans { get; } = new ObservableCollection<LoanItem>();
        public ObservableCollection<AvailableLoanCopy> AvailableCopies { get; } = new ObservableCollection<AvailableLoanCopy>();
        public string Message { get => _message; private set => Set(ref _message, value); }
        public string ReaderUsername { get => _readerUsername; set => Set(ref _readerUsername, value); }
        public string CopyCode { get => _copyCode; set => Set(ref _copyCode, value); }
        public string LoanDays { get => _loanDays; set => Set(ref _loanDays, value); }
        public string CopyQuery { get => _copyQuery; set => Set(ref _copyQuery, value); }
        public bool Busy
        {
            get => _busy;
            private set
            {
                if (!Set(ref _busy, value)) return;
                (RefreshCommand as AsyncCommand)?.Refresh();
                (SearchAvailableCommand as AsyncCommand)?.Refresh();
                (CheckoutCommand as AsyncCommand)?.Refresh();
                (ReturnCommand as AsyncCommand)?.Refresh();
            }
        }
        public LoanItem SelectedLoan
        {
            get => _selectedLoan;
            set
            {
                if (!Set(ref _selectedLoan, value)) return;
                (ReturnCommand as AsyncCommand)?.Refresh();
            }
        }
        public AvailableLoanCopy SelectedCopy
        {
            get => _selectedCopy;
            set
            {
                if (!Set(ref _selectedCopy, value)) return;
                if (value != null) CopyCode = value.CopyCode;
            }
        }
        public ICommand RefreshCommand { get; }
        public ICommand SearchAvailableCommand { get; }
        public ICommand CheckoutCommand { get; }
        public ICommand ReturnCommand { get; }

        private static void Replace<T>(ObservableCollection<T> target, System.Collections.Generic.IEnumerable<T> items)
        {
            target.Clear();
            if (items == null) return;
            foreach (var item in items) target.Add(item);
        }
        private async Task<bool> FetchLoansAsync()
        {
            var response = await _api.GetLoansAsync(_token);
            if (!response.IsSuccess) { Message = response.Message; return false; }
            Replace(Loans, response.Loans);
            SelectedLoan = null;
            return true;
        }
        private async Task<bool> FetchCopiesAsync()
        {
            if (!CanManage) return true;
            var response = await _api.GetAvailableCopiesAsync(_token, CopyQuery);
            if (!response.IsSuccess) { Message = response.Message; return false; }
            Replace(AvailableCopies, response.Copies);
            SelectedCopy = null;
            return true;
        }
        public async Task LoadAsync()
        {
            if (Busy) return;
            Busy = true;
            Message = "Đang tải danh sách mượn / trả...";
            try
            {
                bool loansOk = await FetchLoansAsync();
                bool copiesOk = loansOk && await FetchCopiesAsync();
                if (loansOk && copiesOk)
                    Message = Loans.Count == 0
                        ? "Chưa có lượt mượn sách nào."
                        : "Đã tải " + Loans.Count + " lượt mượn/trả gần nhất (tối đa 100).";
            }
            catch (Exception) { Message = "Không tải được dữ liệu mượn trả lúc này."; }
            finally { Busy = false; }
        }
        private async Task SearchCopiesAsync()
        {
            if (!CanManage || Busy) return;
            if ((CopyQuery ?? "").Length > 80) { Message = "Từ khóa tối đa 80 ký tự."; return; }
            Busy = true;
            Message = "Đang tìm cuốn còn khả dụng...";
            try
            {
                if (await FetchCopiesAsync())
                    Message = AvailableCopies.Count == 0 ? "Không có cuốn nào có thể mượn."
                        : "Tìm thấy " + AvailableCopies.Count + " cuốn còn sẵn (tối đa 100).";
            }
            catch (Exception) { Message = "Không tải được danh sách bản sao."; }
            finally { Busy = false; }
        }
        private async Task CheckoutAsync()
        {
            if (!CanManage || Busy) return;
            int days;
            if (string.IsNullOrWhiteSpace(ReaderUsername) || string.IsNullOrWhiteSpace(CopyCode) ||
                !int.TryParse(LoanDays, out days) || days < 1 || days > 90)
            {
                Message = "Nhập username Reader, mã cuốn và số ngày mượn từ 1 đến 90.";
                return;
            }
            Busy = true;
            Message = "Đang tạo phiếu mượn...";
            try
            {
                var result = await _api.CheckoutAsync(_token, ReaderUsername.Trim(), CopyCode.Trim(), days);
                Message = result.Message;
                if (!result.IsSuccess) return;
                CopyCode = "";
                // Refresh both ObservableCollections in-place for immediate UI updates.
                await FetchLoansAsync();
                await FetchCopiesAsync();
                Message = result.Message;
            }
            catch (Exception) { Message = "Không thể tạo phiếu mượn lúc này."; }
            finally { Busy = false; }
        }
        private async Task ReturnAsync()
        {
            if (!CanManage || Busy || SelectedLoan == null || !SelectedLoan.IsOpen) return;
            long detailId = SelectedLoan.BorrowDetailId;
            Busy = true;
            Message = "Đang ghi nhận trả cuốn...";
            try
            {
                var result = await _api.ReturnAsync(_token, detailId);
                Message = result.Message;
                if (!result.IsSuccess) return;
                await FetchLoansAsync();
                await FetchCopiesAsync();
                Message = result.Message;
            }
            catch (Exception) { Message = "Không thể ghi nhận trả sách lúc này."; }
            finally { Busy = false; }
        }
    }

}
