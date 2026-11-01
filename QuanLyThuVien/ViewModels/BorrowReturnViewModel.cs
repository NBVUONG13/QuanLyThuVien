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
        private LoanRenewalItem _selectedRenewal;
        private string _renewalReviewNote = "";
        public ObservableCollection<LoanRenewalItem> RenewalRequests { get; } = new ObservableCollection<LoanRenewalItem>();
        public string RenewalReviewNote { get => _renewalReviewNote; set => Set(ref _renewalReviewNote, value); }
        public ICommand RequestRenewalCommand { get; }
        public ICommand CancelRenewalCommand { get; }
        public ICommand ApproveRenewalCommand { get; }
        public ICommand RejectRenewalCommand { get; }
        public LoanRenewalItem SelectedRenewal
        {
            get => _selectedRenewal;
            set { if (Set(ref _selectedRenewal, value)) RefreshRenewalCommands(); }
        }
        private void RefreshRenewalCommands()
        {
            (RequestRenewalCommand as AsyncCommand)?.Refresh();
            (CancelRenewalCommand as AsyncCommand)?.Refresh();
            (ApproveRenewalCommand as AsyncCommand)?.Refresh();
            (RejectRenewalCommand as AsyncCommand)?.Refresh();
        }

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
            RequestRenewalCommand = new AsyncCommand(_ => RequestRenewalAsync(),
                _ => IsReader && !Busy && SelectedLoan != null && SelectedLoan.IsOpen);
            CancelRenewalCommand = new AsyncCommand(_ => CancelRenewalAsync(),
                _ => IsReader && !Busy && SelectedRenewal != null && SelectedRenewal.Status == "pending");
            ApproveRenewalCommand = new AsyncCommand(_ => ReviewRenewalAsync(true),
                _ => CanManage && !Busy && SelectedRenewal != null && SelectedRenewal.Status == "pending");
            RejectRenewalCommand = new AsyncCommand(_ => ReviewRenewalAsync(false),
                _ => CanManage && !Busy && SelectedRenewal != null && SelectedRenewal.Status == "pending");
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
                RefreshRenewalCommands();
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
                RefreshRenewalCommands();
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
        private async Task<bool> FetchRenewalsAsync()
        {
            var result = await _api.GetLoanRenewalsAsync(_token);
            if (!result.IsSuccess) { Message = result.Message; return false; }
            Replace(RenewalRequests, result.Requests);
            SelectedRenewal = null;
            return true;
        }
        private async Task RequestRenewalAsync()
        {
            if (!IsReader || Busy || SelectedLoan == null || !SelectedLoan.IsOpen) return;
            var id = SelectedLoan.BorrowDetailId;
            Busy = true;
            try
            {
                var result = await _api.RequestLoanRenewalAsync(_token, id);
                Message = result.Message;
                if (result.IsSuccess) await FetchRenewalsAsync();
            }
            catch (Exception) { Message = "Không gửi được đơn gia hạn."; }
            finally { Busy = false; }
        }
        private async Task CancelRenewalAsync()
        {
            if (!IsReader || Busy || SelectedRenewal == null || SelectedRenewal.Status != "pending") return;
            var id = SelectedRenewal.RequestId;
            Busy = true;
            try
            {
                var result = await _api.CancelLoanRenewalAsync(_token, id);
                Message = result.Message;
                if (result.IsSuccess) await FetchRenewalsAsync();
            }
            catch (Exception) { Message = "Không hủy được đơn gia hạn."; }
            finally { Busy = false; }
        }
        private async Task ReviewRenewalAsync(bool approve)
        {
            if (!CanManage || Busy || SelectedRenewal == null || SelectedRenewal.Status != "pending") return;
            if (!approve && string.IsNullOrWhiteSpace(RenewalReviewNote))
            { Message = "Phải nhập lý do từ chối gia hạn."; return; }
            var id = SelectedRenewal.RequestId;
            Busy = true;
            try
            {
                var result = await _api.ReviewLoanRenewalAsync(_token, id, approve, RenewalReviewNote);
                Message = result.Message;
                if (result.IsSuccess)
                {
                    RenewalReviewNote = "";
                    await FetchRenewalsAsync();
                    await FetchLoansAsync();
                    Message = result.Message;
                }
            }
            catch (Exception) { Message = "Không duyệt được đơn gia hạn."; }
            finally { Busy = false; }
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
                bool renewalOk = loansOk && await FetchRenewalsAsync();
                bool copiesOk = renewalOk && await FetchCopiesAsync();
                if (loansOk && renewalOk && copiesOk)
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

    public sealed class WalkInCounterPage
    {
        public WalkInCounterPage(BorrowReturnViewModel loans)
        {
            Loans = loans ?? throw new ArgumentNullException(nameof(loans));
        }
        public BorrowReturnViewModel Loans { get; }
    }

}
