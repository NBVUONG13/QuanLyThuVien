using System;
using System.Collections.ObjectModel;
using System.Linq;
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
        private object _readerDetailReturnPage;
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
            CounterCommand = new AsyncCommand(_ => NavigateCounterAsync(), _ => CanManage);
            RequestsCommand = new AsyncCommand(_ => NavigateRequestsAsync(), _ => IsAuthenticated);
            LogoutCommand = new AsyncCommand(_ => LogoutAsync());
            BackFromReaderDetailsCommand = new ActionCommand(_ =>
            {
                if (ReaderPanel != null) CurrentPage = _readerDetailReturnPage ?? ReaderPanel;
                _readerDetailReturnPage = null;
            });
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
        public WalkInCounterPage CounterPage { get; private set; }
        public BorrowRequestViewModel Requests { get; private set; }
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
        public ICommand CounterCommand { get; }
        public ICommand RequestsCommand { get; }
        public ICommand LogoutCommand { get; }
        public ICommand PendingCommand { get; }
        public ICommand BackFromReaderDetailsCommand { get; }

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
            (CounterCommand as AsyncCommand)?.Refresh();
            (RequestsCommand as AsyncCommand)?.Refresh();

            // Reuse one BookManagementViewModel for all browsing/search/metadata/copies work.
            Books = new BookManagementViewModel(_token, Role);
            Books.BorrowBookRequested += book => { _ = OpenBorrowRequestFromBookAsync(book); };
            if (IsAdmin) Staff = new HRManagementViewModel(_token);
            ReaderPanel = new ReaderManagementViewModel(_token, Role);
            ReaderPanel.ReaderDetailRequested += (sender, args) =>
            {
                // Navigate to a NEW PAGE instead of a Popup/Window overlay.
                CurrentPage = new ReaderDetailPage(ReaderPanel, BackFromReaderDetailsCommand);
            };
            Loans = new BorrowReturnViewModel(_token, Role);
            CounterPage = new WalkInCounterPage(Loans);
            Loans.IdentityDetailRequested += username => { _ = OpenRequestReaderAsync(username, CounterPage); };
            Requests = new BorrowRequestViewModel(_token, Role);
            Requests.ReaderDetailRequested += username => { _ = OpenRequestReaderAsync(username, Requests); };
            Status = "Đã xác thực phiên: " + Username + " (" + Role + ")";

            // Open the bookstore-style home page immediately after login.
            await NavigateHomeAsync();
        }

        private async Task OpenBorrowRequestFromBookAsync(CatalogBook book)
        {
            if (!IsReader || Requests == null || book == null) return;
            // Show the existing request page first; no checkout or copy assignment here.
            CurrentPage = Requests;
            try
            {
                await Requests.LoadAsync();
                await Requests.PreselectBookAsync(book);
            }
            catch (Exception)
            {
                Status = "Không thêm được sách vào đơn mượn. Hãy mở giỏ và thử lại.";
            }
        }

        private async Task OpenRequestReaderAsync(string username, object returnPage)
        {
            if (!CanManage || ReaderPanel == null || string.IsNullOrWhiteSpace(username)) return;
            _readerDetailReturnPage = returnPage;
            ReaderPanel.Query = username;
            await ReaderPanel.LoadAsync();
            foreach (var r in ReaderPanel.Readers)
            {
                if (string.Equals(r.Username, username, StringComparison.OrdinalIgnoreCase))
                {
                    ReaderPanel.SelectedReader = r;
                    ReaderPanel.OpenReaderDetailsCommand.Execute(r);
                    return;
                }
            }
            // Show Reader list with the search result/error instead of leaving user in place.
            CurrentPage = ReaderPanel;
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
            _readerDetailReturnPage = null;
            CurrentPage = ReaderPanel;
            await ReaderPanel.LoadAsync();
        }

        private Task NavigateCounterAsync()
        {
            if (!CanManage || CounterPage == null) return Task.CompletedTask;
            CurrentPage = CounterPage;
            return Task.CompletedTask;
        }

        private async Task NavigateLoansAsync()
        {
            if (!IsAuthenticated || Loans == null) return;
            CurrentPage = Loans;
            // An order request is NOT a checked-out copy. Show both timelines,
            // but load them independently into their existing ObservableCollections.
            if (IsReader && Requests != null)
                await Task.WhenAll(Loans.LoadAsync(), Requests.LoadHistoryAsync());
            else
                await Loans.LoadAsync();
        }

        private async Task NavigateRequestsAsync()
        {
            if (!IsAuthenticated || Requests == null) return;
            CurrentPage = Requests;
            await Requests.LoadAsync();
        }

        private async Task LogoutAsync()
        {
            if (_loggingOut) return;
            _loggingOut = true;
            try { await _auth.LogoutAsync(_token); }
            finally { LogoutRequested?.Invoke(this, EventArgs.Empty); }
        }
    }

    // Dedicated counter navigation page, reusing the EXISTING BorrowReturnViewModel.
    // No duplicate ViewModel, service or new XAML view is added to the WPF project.
    public sealed class WalkInCounterPage
    {
        public WalkInCounterPage(BorrowReturnViewModel loans)
        {
            Loans = loans ?? throw new ArgumentNullException(nameof(loans));
        }
        public BorrowReturnViewModel Loans { get; }
    }

    // Navigation state for Views/ReaderDetailView.xaml (one real page, no duplicate API logic).
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
        private ReaderProfileItem _selectedReader;
        private ReaderInsightResponse _insights;
        private bool _detailsLoaded, _restrictionBusy;
        private int _detailVersion;
        private string _restrictionReason = "", _decisionMessage = "";
        public ReaderProfileItem SelectedReader
        {
            get => _selectedReader;
            set
            {
                if (!Set(ref _selectedReader, value)) return;
                ++_detailVersion;
                DetailsLoaded = false;
                Insights = null;
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
        private void RefreshAccessCommands()
        {
            (RefreshDetailsCommand as AsyncCommand)?.Refresh();
            (BlockBorrowCommand as AsyncCommand)?.Refresh();
            (UnblockBorrowCommand as AsyncCommand)?.Refresh();
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



    // Step54: lending domain lives in a dedicated VM TYPE, in the existing MainViewModel.cs
    // so the user does not need to add an otherwise empty/duplicate screen
    public sealed class BorrowReturnViewModel : MvvmBase
    {
        private readonly string _token;
        private readonly LoanApiClient _api = new LoanApiClient();
        private string _message = "", _readerUsername = "", _copyCode = "", _loanDays = "14", _copyQuery = "";
        private bool _busy;
        private int _readerSearchVersion, _copySearchVersion;
        private bool _suppressReaderSearch;
        private string _readerSearchMessage = "Nhập tên, username hoặc mã độc giả để xem gợi ý.";
        private string _copySearchMessage = "Gõ tên sách/mã cuốn; bảng bên dưới tự lọc.";
        private bool _showReaderSuggestions;
        private bool _showBookSuggestions;
        private bool _suppressCopySearch;
        private LoanBookSuggestion _selectedBookSuggestion;
        private LoanReaderSuggestion _selectedReader;
        private LoanItem _selectedLoan;
        private AvailableLoanCopy _selectedCopy;
        private int _identityVersion;
        private string _identityPhone = "", _identityCitizen = "", _identityMessage =
            "Nhập đủ SĐT hoặc 12 số CCCD để kiểm tra hồ sơ cũ.";
        private bool _identityBusy, _identityConflict;
        private ReaderIdentityMatch _selectedIdentityMatch;
        private string _walkInFullName = "", _walkInNote = "", _walkInDays = "14";
        private bool _walkInVerified, _identityChecked;
        private bool _walkInExistingMode = true, _walkInExistingConfirmed;
        public bool WalkInExistingMode => _walkInExistingMode;
        public bool WalkInNewMode => !_walkInExistingMode;
        public bool WalkInExistingConfirmed
        {
            get => _walkInExistingConfirmed;
            private set => Set(ref _walkInExistingConfirmed, value);
        }
        public ICommand ShowExistingWalkInCommand { get; }
        public ICommand ShowNewWalkInCommand { get; }

        private void ChangeWalkInMode(bool existing)
        {
            if (!CanManage || Busy || _walkInExistingMode == existing) return;
            _walkInExistingMode = existing;
            Notify(nameof(WalkInExistingMode));
            Notify(nameof(WalkInNewMode));
            WalkInExistingConfirmed = false;
            WalkInVerified = false;
            if (existing) WalkInFullName = "";
            else SelectedIdentityMatch = null;
            Message = existing
                ? "Chế độ hồ sơ cũ: tra cứu, kiểm tra lịch sử, chọn hồ sơ rồi mới gửi đơn."
                : "Chế độ hồ sơ mới: kiểm tra không trùng và xác minh trực tiếp trước khi gửi đơn.";
        }
        private LoanBookSuggestion _walkInSelectedBook;
        private BorrowRequestCartItem _walkInSelectedCartItem;
        public ObservableCollection<BorrowRequestCartItem> WalkInCart { get; } =
            new ObservableCollection<BorrowRequestCartItem>();
        public string WalkInFullName { get => _walkInFullName; set => Set(ref _walkInFullName, value); }
        public string WalkInNote { get => _walkInNote; set => Set(ref _walkInNote, value); }
        public string WalkInDays { get => _walkInDays; set => Set(ref _walkInDays, value); }
        public bool WalkInVerified { get => _walkInVerified; set => Set(ref _walkInVerified, value); }
        public LoanBookSuggestion WalkInSelectedBook
        {
            get => _walkInSelectedBook;
            set
            {
                if (Set(ref _walkInSelectedBook, value))
                    (WalkInAddBookCommand as ActionCommand)?.Refresh();
            }
        }
        public BorrowRequestCartItem WalkInSelectedCartItem
        {
            get => _walkInSelectedCartItem;
            set
            {
                if (Set(ref _walkInSelectedCartItem, value))
                    (WalkInRemoveBookCommand as ActionCommand)?.Refresh();
            }
        }
        public ICommand WalkInAddBookCommand { get; }
        public ICommand WalkInRemoveBookCommand { get; }
        public ICommand WalkInSubmitCommand { get; }
        public ICommand WalkInClearCommand { get; }


        public BorrowReturnViewModel(string token, string role)
        {
            _token = token;
            CanManage = role == "Staff" || role == "Admin";
            IsReader = role == "Reader";
            ShowExistingWalkInCommand = new ActionCommand(_ => ChangeWalkInMode(true), _ => CanManage && !Busy);
            ShowNewWalkInCommand = new ActionCommand(_ => ChangeWalkInMode(false), _ => CanManage && !Busy);
            CheckIdentityCommand = new AsyncCommand(_ => CheckIdentityAsync(++_identityVersion), _ => CanManage && !_identityBusy);
            OpenIdentityDetailCommand = new ActionCommand(_ =>
            {
                if (CanManage && SelectedIdentityMatch != null)
                    IdentityDetailRequested?.Invoke(SelectedIdentityMatch.Username);
            }, _ => CanManage && SelectedIdentityMatch != null);
            UseMatchedReaderCommand = new ActionCommand(_ =>
            {
                var match = SelectedIdentityMatch;
                if (!CanManage || !WalkInExistingMode || !_identityChecked || IdentityBusy || match == null ||
                    IdentityConflict || match.AccountStatus != "active" ||
                    match.BorrowBlocked || match.OverdueCopies > 0) return;
                ReaderUsername = match.Username;
                WalkInExistingConfirmed = true;
                IdentityMessage = "Đã chọn hồ sơ cũ: " + match.FullName +
                    ". Staff cần xác minh danh tính trực tiếp trước khi tiếp nhận.";
            }, _ => CanManage && WalkInExistingMode && _identityChecked && !IdentityBusy &&
                  SelectedIdentityMatch != null && !IdentityConflict &&
                  SelectedIdentityMatch.AccountStatus == "active" &&
                  !SelectedIdentityMatch.BorrowBlocked && SelectedIdentityMatch.OverdueCopies == 0);

            RefreshCommand = new AsyncCommand(_ => LoadAsync(), _ => !Busy);
            SearchAvailableCommand = new AsyncCommand(_ => SearchCopiesAsync(), _ => CanManage && !Busy);
            CheckoutCommand = new AsyncCommand(_ => CheckoutAsync(), _ => CanManage && !Busy);
            ReturnCommand = new AsyncCommand(_ => ReturnAsync(),
                _ => CanManage && !Busy && SelectedLoan != null && SelectedLoan.IsOpen);
            WalkInAddBookCommand = new ActionCommand(_ => AddWalkInBook(),
                _ => CanManage && WalkInSelectedBook != null && WalkInCart.Count < 5);
            WalkInRemoveBookCommand = new ActionCommand(_ =>
            {
                if (WalkInSelectedCartItem == null) return;
                WalkInCart.Remove(WalkInSelectedCartItem);
                WalkInSelectedCartItem = null;
                (WalkInAddBookCommand as ActionCommand)?.Refresh();
            }, _ => CanManage && WalkInSelectedCartItem != null);
            WalkInSubmitCommand = new AsyncCommand(_ => SubmitWalkInAsync(),
                _ => CanManage && !Busy && WalkInCart.Count > 0);
            WalkInClearCommand = new ActionCommand(_ => ClearWalkInForm(), _ => CanManage && !Busy);

        }
        public event Action<string> IdentityDetailRequested;
        public ObservableCollection<ReaderIdentityMatch> IdentityMatches { get; } =
            new ObservableCollection<ReaderIdentityMatch>();
        public ICommand CheckIdentityCommand { get; }
        public ICommand OpenIdentityDetailCommand { get; }
        public ICommand UseMatchedReaderCommand { get; }
        public bool IdentityBusy
        {
            get => _identityBusy;
            private set
            {
                if (!Set(ref _identityBusy, value)) return;
                (CheckIdentityCommand as AsyncCommand)?.Refresh();
            }
        }
        public bool IdentityConflict { get => _identityConflict; private set => Set(ref _identityConflict, value); }
        public ReaderIdentityMatch SelectedIdentityMatch
        {
            get => _selectedIdentityMatch;
            set
            {
                if (!Set(ref _selectedIdentityMatch, value)) return;
                WalkInExistingConfirmed = false;
                (OpenIdentityDetailCommand as ActionCommand)?.Refresh();
                (UseMatchedReaderCommand as ActionCommand)?.Refresh();
            }
        }
        public string IdentityMessage { get => _identityMessage; private set => Set(ref _identityMessage, value); }
        public string IdentityPhone
        {
            get => _identityPhone;
            set { if (Set(ref _identityPhone, value)) ScheduleIdentityLookup(); }
        }
        public string IdentityCitizen
        {
            get => _identityCitizen;
            set { if (Set(ref _identityCitizen, value)) ScheduleIdentityLookup(); }
        }
        public bool CanManage { get; }
        public bool IsReader { get; }
        public ObservableCollection<LoanItem> Loans { get; } = new ObservableCollection<LoanItem>();
        public ObservableCollection<AvailableLoanCopy> AvailableCopies { get; } = new ObservableCollection<AvailableLoanCopy>();
        public ObservableCollection<LoanReaderSuggestion> ReaderSuggestions { get; } =
            new ObservableCollection<LoanReaderSuggestion>();
        public ObservableCollection<LoanBookSuggestion> BookSuggestions { get; } =
            new ObservableCollection<LoanBookSuggestion>();
        public bool ShowBookSuggestions { get => _showBookSuggestions; set => Set(ref _showBookSuggestions, value); }
        public LoanBookSuggestion SelectedBookSuggestion
        {
            get => _selectedBookSuggestion;
            set
            {
                if (!Set(ref _selectedBookSuggestion, value) || value == null) return;
                // Selecting a suggested TITLE is not the same as selecting a physical copy.
                _suppressCopySearch = true;
                CopyQuery = value.Title;
                _suppressCopySearch = false;
                ++_copySearchVersion; // Discard older autocomplete requests.
                ShowBookSuggestions = false;
                BookSuggestions.Clear();
                CopyCode = value.AvailableCopies > 0 ? value.FirstAvailableCopyCode : "";
                CopySearchMessage = value.AvailableCopies > 0
                    ? "Đã chọn: " + value.Title + ". Gợi ý mã cuốn: " + CopyCode + "."
                    : "Đã chọn: " + value.Title + ". Chưa có cuốn vật lý có thể mượn; cần nhập kho trước.";
                // Refresh the list of AVAILABLE copies for this selected book.
                _ = ShowCopiesForBookAsync(value.Title, _copySearchVersion);
            }
        }

        public string ReaderSearchMessage { get => _readerSearchMessage; private set => Set(ref _readerSearchMessage, value); }
        public string CopySearchMessage { get => _copySearchMessage; private set => Set(ref _copySearchMessage, value); }
        public bool ShowReaderSuggestions { get => _showReaderSuggestions; set => Set(ref _showReaderSuggestions, value); }
        public LoanReaderSuggestion SelectedReader
        {
            get => _selectedReader;
            set
            {
                if (!Set(ref _selectedReader, value) || value == null) return;
                _suppressReaderSearch = true;
                ReaderUsername = value.Username; // Exact username required by checkout API.
                _suppressReaderSearch = false;
                _readerSearchVersion++;
                ReaderSuggestions.Clear();
                ShowReaderSuggestions = false;
                ReaderSearchMessage = "Đã chọn: " + value.FullName + " (@" + value.Username + ").";
            }
        }
        public string Message { get => _message; private set => Set(ref _message, value); }
        public string ReaderUsername
        {
            get => _readerUsername;
            set
            {
                if (!Set(ref _readerUsername, value) || _suppressReaderSearch) return;
                int version = ++_readerSearchVersion;
                ReaderSuggestions.Clear();
                ShowReaderSuggestions = false;
                _selectedReader = null; Notify(nameof(SelectedReader));
                if (CanManage) _ = SuggestReadersAsync(value, version);
            }
        }
        public string CopyCode { get => _copyCode; set => Set(ref _copyCode, value); }
        public string LoanDays { get => _loanDays; set => Set(ref _loanDays, value); }
        public string CopyQuery
        {
            get => _copyQuery;
            set
            {
                if (!Set(ref _copyQuery, value) || _suppressCopySearch) return;
                int version = ++_copySearchVersion;
                BookSuggestions.Clear();
                ShowBookSuggestions = false;
                _selectedBookSuggestion = null;
                Notify(nameof(SelectedBookSuggestion));
                if (string.IsNullOrWhiteSpace(value))
                {
                    CopySearchMessage = "Gõ tên sách để xem gợi ý ngay bên dưới.";
                    return;
                }
                if (CanManage) _ = SuggestCopiesAsync(value, version);
            }
        }
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
                (WalkInSubmitCommand as AsyncCommand)?.Refresh();
                (ShowExistingWalkInCommand as ActionCommand)?.Refresh();
                (ShowNewWalkInCommand as ActionCommand)?.Refresh();
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
                if (value != null)
                {
                    CopyCode = value.CopyCode;
                    ShowBookSuggestions = false;
                }
            }
        }
        public ICommand RefreshCommand { get; }
        public ICommand SearchAvailableCommand { get; }
        public ICommand CheckoutCommand { get; }
        public ICommand ReturnCommand { get; }

        private void ClearWalkInForm()
        {
            ++_identityVersion; // Cancel any in-flight identity suggestion response.
            _identityChecked = false;
            WalkInExistingConfirmed = false;
            _identityCitizen = ""; Notify(nameof(IdentityCitizen));
            _identityPhone = ""; Notify(nameof(IdentityPhone));
            IdentityMatches.Clear(); SelectedIdentityMatch = null;
            IdentityConflict = false;
            WalkInFullName = ""; WalkInNote = ""; WalkInVerified = false;
            WalkInCart.Clear(); WalkInSelectedCartItem = null; WalkInSelectedBook = null;
            IdentityMessage = "Đã xóa SĐT/CCCD trong form. Cần tra cứu lại khi tiếp nhận khách.";
            Message = "Đã hủy nhập đơn tại quầy; chưa có đơn nào được ghi.";
            (WalkInSubmitCommand as AsyncCommand)?.Refresh();
            (WalkInAddBookCommand as ActionCommand)?.Refresh();
        }

        private void AddWalkInBook()
        {
            var book = WalkInSelectedBook;
            if (!CanManage || book == null) return;
            int days;
            if (!int.TryParse(WalkInDays, out days) || days < 1 || days > 90)
            { Message = "Số ngày mượn phải từ 1 đến 90."; return; }
            if (WalkInCart.Count >= 5) { Message = "Một đơn chỉ tối đa 5 đầu sách."; return; }
            if (WalkInCart.Any(x => x.BookId == book.BookId))
            { Message = "Sách này đã nằm trong đơn tại quầy."; return; }
            WalkInCart.Add(new BorrowRequestCartItem
            {
                BookId = book.BookId,
                Title = book.Title,
                Authors = book.Authors,
                RequestedDays = days,
                AvailableCopies = book.AvailableCopies
            });
            Message = "Đã thêm sách vào đơn tại quầy. Chưa gửi yêu cầu.";
            (WalkInAddBookCommand as ActionCommand)?.Refresh();
            (WalkInSubmitCommand as AsyncCommand)?.Refresh();
        }

        private async Task SubmitWalkInAsync()
        {
            if (!CanManage || Busy) return;
            if (!_identityChecked || IdentityBusy)
            { Message = "Phải chờ KIỂM TRA TRÙNG SĐT/CCCD hoàn tất trước khi lập đơn."; return; }
            if (IdentityConflict || IdentityMatches.Count > 1)
            { Message = "Có xung đột danh tính; không được tự chọn hoặc tạo Guest."; return; }
            var selected = SelectedIdentityMatch;
            if (WalkInExistingMode && (selected == null || !WalkInExistingConfirmed))
            { Message = "Chọn hồ sơ cũ và bấm DÙNG HỒ SƠ NÀY trước khi gửi đơn."; return; }
            if (WalkInNewMode && (IdentityMatches.Count != 0 || selected != null))
            { Message = "Thông tin đã trùng hồ sơ. Chuyển sang HỒ SƠ CŨ để kiểm tra và dùng lại."; return; }
            if (IdentityMatches.Count == 1 && selected == null)
            { Message = "Đã có hồ sơ trùng. Chọn hồ sơ và XEM LỊCH SỬ trước khi dùng lại."; return; }
            if (selected != null && (selected.AccountStatus != "active" || selected.BorrowBlocked ||
                                   selected.OverdueCopies > 0))
            { Message = "Hồ sơ cũ đang bị hạn chế; xem lịch sử trước khi tiếp nhận."; return; }
            if (selected == null && (string.IsNullOrWhiteSpace(WalkInFullName) || WalkInFullName.Trim().Length < 2))
            { Message = "Nhập họ tên khách mới trước khi lập đơn."; return; }
            if (!WalkInVerified)
            { Message = "Staff phải xác nhận đã kiểm tra giấy tờ gốc hoặc quyền sở hữu SĐT thực tế."; return; }
            if (WalkInCart.Count < 1)
            { Message = "Thêm ít nhất một đầu sách vào giỏ."; return; }
            Busy = true;
            try
            {
                var lines = WalkInCart.Select(x => new BorrowRequestLine
                {
                    BookId = x.BookId,
                    RequestedDays = x.RequestedDays
                }).ToList();
                var result = await _api.CreateWalkInRequestAsync(_token, selected?.UserId,
                    WalkInFullName, IdentityPhone, IdentityCitizen, WalkInVerified, lines, WalkInNote);
                Message = result?.Message ?? "Không gửi được đơn tại quầy.";
                if (result == null || !result.IsSuccess) return;
                WalkInCart.Clear(); WalkInSelectedCartItem = null; WalkInSelectedBook = null;
                WalkInFullName = ""; WalkInNote = ""; WalkInVerified = false;
                // Clear sensitive CCCD from the VM as soon as the transaction completes.
                _identityCitizen = ""; Notify(nameof(IdentityCitizen));
                _identityPhone = ""; Notify(nameof(IdentityPhone));
                _identityChecked = false; IdentityMatches.Clear(); SelectedIdentityMatch = null;
                WalkInExistingConfirmed = false;
                (WalkInAddBookCommand as ActionCommand)?.Refresh();
                Message += " Vào 'ĐƠN MƯỢN READER GỬI' để xem/xử lý cùng các đơn từ ứng dụng.";
            }
            catch (Exception) { Message = "Không tạo được đơn tại quầy. Kiểm tra API."; }
            finally { Busy = false; (WalkInSubmitCommand as AsyncCommand)?.Refresh(); }
        }

        private void ScheduleIdentityLookup()
        {
            int version = ++_identityVersion;
            _identityChecked = false;
            WalkInExistingConfirmed = false;
            IdentityMatches.Clear();
            SelectedIdentityMatch = null;
            IdentityConflict = false;
            IdentityMessage = "Nhập đủ SĐT hoặc CCCD để tra cứu tự động.";
            if (CanManage) _ = IdentityLookupDebouncedAsync(version);
        }
        private async Task IdentityLookupDebouncedAsync(int version)
        {
            try
            {
                await Task.Delay(500);
                if (version != _identityVersion) return;
                await CheckIdentityAsync(version);
            }
            catch (Exception)
            { if (version == _identityVersion) IdentityMessage = "Không tra cứu được hồ sơ."; }
        }
        private async Task CheckIdentityAsync(int version)
        {
            if (!CanManage) return;
            string phone = (IdentityPhone ?? "").Trim();
            string id = (IdentityCitizen ?? "").Trim();
            string digits = new string(phone.Where(char.IsDigit).ToArray());
            bool phonePresent = phone.Length > 0, idPresent = id.Length > 0;
            if ((!phonePresent && !idPresent) ||
                (phonePresent && !(digits.Length == 10 ||
                    (digits.Length == 11 && digits.StartsWith("84", StringComparison.Ordinal)))) ||
                (idPresent && (id.Length != 12 || id.Any(c => c < '0' || c > '9'))))
            {
                if (version == _identityVersion)
                    IdentityMessage = "Nhập đúng 10 số SĐT (hoặc +84) và/hoặc 12 số CCCD. Chưa gửi tra cứu khi đang nhập dở.";
                return;
            }
            _identityChecked = false;
            WalkInExistingConfirmed = false;
            IdentityBusy = true;
            if (version == _identityVersion) IdentityMessage = "Đang kiểm tra hồ sơ cũ...";
            try
            {
                var result = await _api.CheckReaderIdentityAsync(_token, phone, id);
                if (version != _identityVersion) return;
                IdentityMatches.Clear();
                IdentityConflict = false;
                if (result == null || !result.IsSuccess)
                { IdentityMessage = result?.Message ?? "Lỗi tra cứu."; return; }
                foreach (var match in result.Matches ?? new System.Collections.Generic.List<ReaderIdentityMatch>())
                    IdentityMatches.Add(match);
                _identityChecked = true;
                IdentityConflict = result.Conflict;
                IdentityMessage = IdentityConflict
                    ? "⛔ XUNG ĐỘT SĐT/CCCD: không được tự ghép/chọn hồ sơ. Staff phải xác minh tại quầy."
                    : IdentityMatches.Count > 0
                    ? "⚠ Đã có " + IdentityMatches.Count +
                      " hồ sơ trùng! Chọn một dòng và XEM LỊCH SỬ trước khi tiếp nhận."
                    : "Không tìm thấy hồ sơ trùng; CHƯA xác nhận được khách mới. Phải kiểm tra trực tiếp.";
            }
            catch (Exception)
            { if (version == _identityVersion) IdentityMessage = "Không thể tra cứu. Không tạo hồ sơ mới khi tra cứu thất bại."; }
            finally
            {
                if (version == _identityVersion)
                {
                    // Held in this VM only until the Staff submits/clears intake;
                    // never persisted by WPF. Needed for the create transaction to
                    // re-check CCCD fingerprint against the exact same identifier.
                    IdentityBusy = false;
                    (UseMatchedReaderCommand as ActionCommand)?.Refresh();
                }
            }
        }

        // Debounce on UI synchronization context; stale HTTP replies must never overwrite newer text.
        private async Task SuggestReadersAsync(string query, int version)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                ReaderSearchMessage = "Nhập tên, username hoặc mã độc giả để xem gợi ý.";
                return;
            }
            try
            {
                await Task.Delay(260);
                if (version != _readerSearchVersion) return;
                var result = await _api.GetReaderSuggestionsAsync(_token, query);
                if (version != _readerSearchVersion) return;
                if (!result.IsSuccess)
                {
                    ReaderSearchMessage = result.Message ?? "Lỗi tìm Reader.";
                    return;
                }
                Replace(ReaderSuggestions, result.Readers);
                ShowReaderSuggestions = ReaderSuggestions.Count > 0;
                ReaderSearchMessage = ReaderSuggestions.Count == 0
                    ? "Không có độc giả phù hợp."
                    : "Chọn đúng độc giả bên dưới (tối đa 30 gợi ý).";
            }
            catch (Exception)
            {
                if (version == _readerSearchVersion) ReaderSearchMessage = "Không tải được gợi ý độc giả.";
            }
        }

        private async Task SuggestCopiesAsync(string query, int version)
        {
            try
            {
                await Task.Delay(260);
                if (version != _copySearchVersion) return;
                // Suggestions search all CATALOGUE books; copy list is a separate API call.
                var booksTask = _api.GetBookSuggestionsAsync(_token, query);
                var copiesTask = _api.GetAvailableCopiesAsync(_token, query);
                await Task.WhenAll(booksTask, copiesTask);
                if (version != _copySearchVersion) return;
                var books = await booksTask;
                var copies = await copiesTask;
                if (!books.IsSuccess)
                {
                    ShowBookSuggestions = false;
                    CopySearchMessage = books.Message ?? "Lỗi tìm đầu sách.";
                    return;
                }
                Replace(BookSuggestions, books.Books);
                ShowBookSuggestions = BookSuggestions.Count > 0;
                if (copies.IsSuccess)
                {
                    Replace(AvailableCopies, copies.Copies);
                    SelectedCopy = null;
                }
                CopySearchMessage = BookSuggestions.Count == 0
                    ? "Không có đầu sách nào khớp từ khóa."
                    : "Có " + BookSuggestions.Count + " đầu sách phù hợp. Chọn từ dropdown; cuốn có thể mượn: "
                      + (copies.IsSuccess ? AvailableCopies.Count.ToString() : "chưa tải được") + ".";
            }
            catch (Exception)
            {
                if (version == _copySearchVersion)
                {
                    ShowBookSuggestions = false;
                    CopySearchMessage = "Không tải được gợi ý sách. Kiểm tra kết nối API.";
                }
            }
        }

        private async Task ShowCopiesForBookAsync(string title, int version)
        {
            try
            {
                var result = await _api.GetAvailableCopiesAsync(_token, title);
                if (version != _copySearchVersion) return;
                if (!result.IsSuccess) { CopySearchMessage = result.Message; return; }
                // Copy search may also match other titles; still allow staff to select manually.
                Replace(AvailableCopies, result.Copies);
                SelectedCopy = null;
            }
            catch (Exception)
            {
                if (version == _copySearchVersion) CopySearchMessage = "Không tải được cuốn vật lý.";
            }
        }

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
            CopySearchMessage = AvailableCopies.Count == 0
                ? "Không có cuốn còn sẵn phù hợp."
                : "Có " + AvailableCopies.Count + " cuốn còn sẵn (tối đa 100).";
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
                ReaderSuggestions.Clear(); ShowReaderSuggestions = false;
                BookSuggestions.Clear(); ShowBookSuggestions = false;
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


    // Step58: Reader's request cart. Requests are NOT loans; Staff approval is a later module.
    // This is a VM TYPE in the existing MainViewModel.cs, not another screen or duplicate service.
    public sealed class BorrowRequestViewModel : MvvmBase
    {
        private readonly string _token;
        private readonly LoanApiClient _api = new LoanApiClient();
        private bool _busy, _suppressSearch, _showSuggestions;
        private int _searchVersion;
        private string _message = "", _searchText = "", _daysText = "14", _note = "";
        private string _catalogStatus = "Đang tải danh mục đầu sách...";
        private string _reviewNote = "";
        private LoanBookSuggestion _selectedSuggestion;
        private BorrowRequestCartItem _selectedCartItem;
        private BorrowRequestSummary _selectedRequest;

        public BorrowRequestViewModel(string token, string role)
        {
            _token = token;
            IsReader = role == "Reader";
            CanManage = role == "Staff" || role == "Admin";
            RefreshCommand = new AsyncCommand(_ => LoadHistoryAsync(), _ => !Busy);
            AddBookCommand = new ActionCommand(_ => AddBook(),
                _ => IsReader && !Busy && SelectedSuggestion != null);
            RemoveBookCommand = new ActionCommand(_ => RemoveBook(),
                _ => IsReader && !Busy && SelectedCartItem != null);
            SubmitCommand = new AsyncCommand(_ => SubmitAsync(),
                _ => IsReader && !Busy && Cart.Count > 0);
            CancelRequestCommand = new AsyncCommand(_ => CancelAsync(),
                _ => IsReader && !Busy && SelectedRequest != null && SelectedRequest.CanCancel);
            ApproveRequestCommand = new AsyncCommand(_ => ReviewAsync(true),
                _ => CanManage && !Busy && SelectedRequest != null && SelectedRequest.Status == "pending");
            RejectRequestCommand = new AsyncCommand(_ => ReviewAsync(false),
                _ => CanManage && !Busy && SelectedRequest != null && SelectedRequest.Status == "pending");
            ViewReaderCommand = new ActionCommand(parameter =>
            {
                if (!CanManage) return;
                var target = parameter as BorrowRequestSummary ?? SelectedRequest;
                if (target != null) ReaderDetailRequested?.Invoke(target.ReaderUsername);
            }, _ => CanManage);
        }
        public bool IsReader { get; }
        public bool CanManage { get; }
        public event Action<string> ReaderDetailRequested;
        public string ReviewNote { get => _reviewNote; set => Set(ref _reviewNote, value); }
        public ICommand ApproveRequestCommand { get; }
        public ICommand RejectRequestCommand { get; }
        public ICommand ViewReaderCommand { get; }
        public ObservableCollection<LoanBookSuggestion> Suggestions { get; } =
            new ObservableCollection<LoanBookSuggestion>();
        public ObservableCollection<BorrowRequestCartItem> Cart { get; } =
            new ObservableCollection<BorrowRequestCartItem>();
        public ObservableCollection<BorrowRequestSummary> Requests { get; } =
            new ObservableCollection<BorrowRequestSummary>();
        public ICommand RefreshCommand { get; }
        public ICommand AddBookCommand { get; }
        public ICommand RemoveBookCommand { get; }
        public ICommand SubmitCommand { get; }
        public ICommand CancelRequestCommand { get; }
        public string Message { get => _message; private set => Set(ref _message, value); }
        public string CatalogStatus { get => _catalogStatus; private set => Set(ref _catalogStatus, value); }
        public string Note { get => _note; set => Set(ref _note, value); }
        public string DaysText { get => _daysText; set => Set(ref _daysText, value); }
        public bool ShowSuggestions { get => _showSuggestions; private set => Set(ref _showSuggestions, value); }
        public bool Busy
        {
            get => _busy;
            private set
            {
                if (!Set(ref _busy, value)) return;
                RefreshCommands();
            }
        }
        public string SearchText
        {
            get => _searchText;
            set
            {
                if (!Set(ref _searchText, value) || _suppressSearch) return;
                int version = ++_searchVersion;
                Suggestions.Clear();
                ShowSuggestions = false;
                SelectedSuggestion = null;
                if (IsReader)
                {
                    CatalogStatus = "Đang tìm đầu sách...";
                    _ = SearchAsync(value ?? "", version);
                }
            }
        }
        public LoanBookSuggestion SelectedSuggestion
        {
            get => _selectedSuggestion;
            set
            {
                if (!Set(ref _selectedSuggestion, value)) return;
                (AddBookCommand as ActionCommand)?.Refresh();
                if (value == null) return;
                _suppressSearch = true;
                SearchText = value.Title;
                _suppressSearch = false;
                ++_searchVersion;
                ShowSuggestions = false;
            }
        }
        public BorrowRequestCartItem SelectedCartItem
        {
            get => _selectedCartItem;
            set
            {
                if (Set(ref _selectedCartItem, value))
                    (RemoveBookCommand as ActionCommand)?.Refresh();
            }
        }
        public BorrowRequestSummary SelectedRequest
        {
            get => _selectedRequest;
            set
            {
                if (Set(ref _selectedRequest, value))
                {
                    (CancelRequestCommand as AsyncCommand)?.Refresh();
                    (ApproveRequestCommand as AsyncCommand)?.Refresh();
                    (RejectRequestCommand as AsyncCommand)?.Refresh();
                }
            }
        }
        private void RefreshCommands()
        {
            (RefreshCommand as AsyncCommand)?.Refresh();
            (AddBookCommand as ActionCommand)?.Refresh();
            (RemoveBookCommand as ActionCommand)?.Refresh();
            (SubmitCommand as AsyncCommand)?.Refresh();
            (CancelRequestCommand as AsyncCommand)?.Refresh();
            (ApproveRequestCommand as AsyncCommand)?.Refresh();
            (RejectRequestCommand as AsyncCommand)?.Refresh();
        }
        private async Task SearchAsync(string query, int version)
        {
            try
            {
                await Task.Delay(260);
                if (version != _searchVersion) return;
                if (query.Length > 80)
                { CatalogStatus = "Từ khóa tối đa 80 ký tự."; return; }
                var result = await _api.GetRequestBookSuggestionsAsync(_token, query);
                if (version != _searchVersion) return;
                if (!result.IsSuccess)
                { CatalogStatus = result.Message ?? "Lỗi tải danh mục."; return; }
                Replace(Suggestions, result.Books);
                ShowSuggestions = Suggestions.Count > 0;
                CatalogStatus = Suggestions.Count == 0
                    ? "Không có đầu sách phù hợp. Hãy thử từ khóa khác."
                    : (string.IsNullOrWhiteSpace(query)
                        ? "Danh mục: " + Suggestions.Count + " đầu sách. Chọn sách để thêm vào đơn."
                        : "Tìm thấy " + Suggestions.Count + " đầu sách phù hợp.");
            }
            catch (Exception) { if (version == _searchVersion) CatalogStatus = "Không tải được đầu sách."; }
        }
        // Entry from Home book detail: resolve the exact bookId from the existing
        // request-suggestion API before adding it to the cart (no fabricated stock count).
        public async Task PreselectBookAsync(CatalogBook book)
        {
            if (!IsReader || book == null) return;
            foreach (var line in Cart)
                if (line.BookId == book.BookId)
                {
                    Message = "Sách này đã nằm trong giỏ mượn của bạn.";
                    return;
                }
            if (Cart.Count >= 5)
            {
                Message = "Giỏ đã có 5 đầu sách, hãy bỏ một sách trước.";
                return;
            }
            try
            {
                var data = await _api.GetRequestBookSuggestionsAsync(_token, book.Title ?? "");
                if (!data.IsSuccess)
                {
                    Message = data.Message ?? "Không tải được đầu sách.";
                    return;
                }
                foreach (var candidate in data.Books)
                {
                    if (candidate.BookId != book.BookId) continue;
                    SelectedSuggestion = candidate;
                    AddBook();
                    return;
                }
                Message = "Đầu sách vừa chọn chưa có trong danh mục yêu cầu.";
            }
            catch (Exception) { Message = "Không thêm được đầu sách vào giỏ."; }
        }

        private void AddBook()
        {
            if (!IsReader || Busy || SelectedSuggestion == null) return;
            int days;
            if (!int.TryParse(DaysText, out days) || days < 1 || days > 90)
            { Message = "Nhập số ngày mượn từ 1 đến 90."; return; }
            if (Cart.Count >= 5)
            { Message = "Mỗi đơn tối đa 5 đầu sách."; return; }
            var book = SelectedSuggestion;
            foreach (var line in Cart)
                if (line.BookId == book.BookId)
                { Message = "Đầu sách này đã có trong đơn."; return; }
            Cart.Add(new BorrowRequestCartItem
            {
                BookId = book.BookId,
                Title = book.Title,
                Authors = book.Authors,
                RequestedDays = days,
                AvailableCopies = book.AvailableCopies
            });
            SelectedSuggestion = null;
            _suppressSearch = true;
            SearchText = "";
            _suppressSearch = false;
            int version = ++_searchVersion;
            Suggestions.Clear(); ShowSuggestions = false;
            CatalogStatus = "Đang tải lại danh mục...";
            _ = SearchAsync("", version);
            Message = "Đã thêm vào đơn (" + Cart.Count + "/5). Chưa gửi yêu cầu.";
            RefreshCommands();
        }
        private void RemoveBook()
        {
            if (SelectedCartItem == null) return;
            Cart.Remove(SelectedCartItem);
            SelectedCartItem = null;
            RefreshCommands();
            Message = "Đã bỏ đầu sách khỏi đơn.";
        }
        private static void Replace<T>(ObservableCollection<T> target,
            System.Collections.Generic.IEnumerable<T> source)
        {
            target.Clear();
            if (source == null) return;
            foreach (var item in source) target.Add(item);
        }
        private async Task<bool> ReloadAsync()
        {
            var response = await _api.GetBorrowRequestsAsync(_token);
            if (!response.IsSuccess) { Message = response.Message; return false; }
            Replace(Requests, response.Requests);
            SelectedRequest = null;
            return true;
        }
        // Only refresh the submitted orders list. Do not reload book suggestions
        // when Reader opens the history page; preserve the unfinished cart.
        public async Task LoadHistoryAsync()
        {
            if (Busy) return;
            Busy = true;
            Message = "Đang tải các đơn đã đặt...";
            try
            {
                if (await ReloadAsync())
                    Message = Requests.Count == 0
                        ? "Bạn chưa có đơn mượn nào."
                        : "Có " + Requests.Count + " đơn đã đặt; chọn đơn để xem trạng thái hoặc hủy khi đang chờ.";
            }
            catch (Exception) { Message = "Không tải được lịch sử đơn mượn. Kiểm tra API."; }
            finally { Busy = false; }
        }

        public async Task LoadAsync()
        {
            if (Busy) return;
            Busy = true;
            Message = "Đang tải đơn mượn...";
            try
            {
                if (await ReloadAsync())
                    Message = IsReader
                        ? "Đơn của bạn: " + Requests.Count + " (tối đa 100)."
                        : "Các yêu cầu mới nhất: " + Requests.Count + " (tối đa 100).";
                // Reader must see REAL BOOK TITLES as soon as the page opens,
                // even if there are zero physical copies and SearchText is empty.
                if (IsReader)
                {
                    CatalogStatus = "Đang tải danh mục...";
                    await SearchAsync(SearchText ?? "", ++_searchVersion);
                }
            }
            catch (Exception) { Message = "Không tải được danh sách đơn mượn."; }
            finally { Busy = false; }
        }
        private async Task SubmitAsync()
        {
            if (!IsReader || Busy || Cart.Count == 0) return;
            Busy = true;
            Message = "Đang gửi yêu cầu...";
            try
            {
                var items = new System.Collections.Generic.List<BorrowRequestLine>();
                foreach (var item in Cart)
                    items.Add(new BorrowRequestLine
                    {
                        BookId = item.BookId,
                        RequestedDays = item.RequestedDays
                    });
                var result = await _api.SubmitBorrowRequestAsync(_token, items, Note);
                Message = result.Message;
                if (!result.IsSuccess) return;
                Cart.Clear(); SelectedCartItem = null; Note = "";
                await ReloadAsync();
                Message = result.Message;
            }
            catch (Exception) { Message = "Không gửi được yêu cầu."; }
            finally { Busy = false; }
        }
        private async Task ReviewAsync(bool approve)
        {
            if (!CanManage || Busy || SelectedRequest == null || SelectedRequest.Status != "pending") return;
            if (!approve && string.IsNullOrWhiteSpace(ReviewNote))
            { Message = "Vui lòng nhập lý do từ chối đơn."; return; }
            long id = SelectedRequest.RequestId;
            Busy = true;
            Message = approve ? "Đang kiểm tra kho và duyệt toàn bộ đơn..." : "Đang từ chối đơn...";
            try
            {
                var result = await _api.ReviewBorrowRequestAsync(_token, id, approve, ReviewNote);
                Message = result.Message;
                if (!result.IsSuccess) return;
                ReviewNote = "";
                await ReloadAsync();
                Message = result.Message;
            }
            catch (Exception) { Message = "Không xử lý được đơn. Kiểm tra API."; }
            finally { Busy = false; }
        }

        private async Task CancelAsync()
        {
            if (!IsReader || Busy || SelectedRequest == null || !SelectedRequest.CanCancel) return;
            var id = SelectedRequest.RequestId;
            Busy = true;
            try
            {
                var result = await _api.CancelBorrowRequestAsync(_token, id);
                Message = result.Message;
                if (!result.IsSuccess) return;
                await ReloadAsync();
                Message = result.Message;
            }
            catch (Exception) { Message = "Không hủy được đơn."; }
            finally { Busy = false; }
        }
    }

}
