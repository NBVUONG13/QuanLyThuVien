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
    public sealed class MainViewModel : MvvmBase
    {
        private readonly string _token;
        private readonly AuthApiClient _auth = new AuthApiClient();
        private object _currentPage;
        private object _readerDetailReturnPage;
        private string _username = "", _role = "", _status = "Đang xác minh phiên...";
        private bool _authenticated, _loggingOut, _isAssistantOpen, _isNotificationOpen;

        public event EventHandler LogoutRequested;

        public MainViewModel(string token)
        {
            _token = token;
            Dashboard = new DashboardViewModel(token);
            HomeCommand = new AsyncCommand(_ => NavigateHomeAsync());
            InventoryCommand = new AsyncCommand(_ => NavigateInventoryAsync(), _ => CanManage);
            MapCommand = new AsyncCommand(_ => NavigateMapAsync(), _ => IsAuthenticated);
            DashboardCommand = new AsyncCommand(_ => NavigateDashboardAsync());
            ReportsCommand = new AsyncCommand(_ => NavigateReportsAsync(), _ => IsAdmin);
            AssistantCommand = new AsyncCommand(_ => NavigateAssistantAsync(), _ => IsAuthenticated);
            StaffCommand = new AsyncCommand(_ => NavigateStaffAsync(), _ => IsAdmin);
            AttendanceCommand = new AsyncCommand(_ => NavigateAttendanceAsync(), _ => CanManage);
            ReaderCommand = new AsyncCommand(_ => NavigateReadersAsync(), _ => IsAuthenticated);
            LoansCommand = new AsyncCommand(_ => NavigateLoansAsync(), _ => IsAuthenticated);
            CounterCommand = new AsyncCommand(_ => NavigateCounterAsync(), _ => CanManage);
            RequestsCommand = new AsyncCommand(_ => NavigateRequestsAsync(), _ => IsAuthenticated);
            NotificationsCommand = new AsyncCommand(_ => NavigateNotificationsAsync(), _ => IsReader);
            FinesCommand = new AsyncCommand(_ => NavigateFinesAsync(), _ => IsAuthenticated);
            PolicyCommand = new AsyncCommand(_ => NavigatePolicyAsync(), _ => IsAdmin);
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
        public LibraryReportsViewModel ReportsPage { get; private set; }
        public BookAssistantViewModel AssistantPage { get; private set; }
        public BookManagementViewModel Books { get; private set; }
        public LibraryMapViewModel MapPage { get; private set; }
        public HRManagementViewModel Staff { get; private set; }
        public ReaderManagementViewModel ReaderPanel { get; private set; }
        public BorrowReturnViewModel Loans { get; private set; }
        public WalkInCounterPage CounterPage { get; private set; }
        public BorrowRequestViewModel Requests { get; private set; }
        public NotificationCenterViewModel Notifications { get; private set; }
        public FineManagementViewModel FinePage { get; private set; }
        public LibraryPolicyViewModel PolicyPage { get; private set; }
        private string _notificationsMenuText = "THÔNG BÁO";
        public string NotificationsMenuText { get => _notificationsMenuText; private set => Set(ref _notificationsMenuText, value); }
        public object CurrentPage { get => _currentPage; private set => Set(ref _currentPage, value); }
        // True only when the Reader opens the floating chat panel; keeps CurrentPage unchanged.
        public bool IsAssistantOpen { get => _isAssistantOpen; private set => Set(ref _isAssistantOpen, value); }
        // Popup IsOpen is TwoWay: clicking outside automatically closes it and updates this value.
        public bool IsNotificationOpen { get => _isNotificationOpen; set => Set(ref _isNotificationOpen, value); }
        public bool HasUnreadNotifications => Notifications != null && Notifications.UnreadCount > 0;
        public string Username { get => _username; private set => Set(ref _username, value); }
        public string Role { get => _role; private set => Set(ref _role, value); }
        public string Status { get => _status; private set => Set(ref _status, value); }
        public bool IsAuthenticated { get => _authenticated; private set => Set(ref _authenticated, value); }
        public bool IsReader => IsAuthenticated && Role == "Reader";
        public bool IsAdmin => IsAuthenticated && Role == "Admin";
        public bool CanManage => IsAuthenticated && (Role == "Staff" || Role == "Admin");

        public ICommand HomeCommand { get; }
        public ICommand InventoryCommand { get; }
        public ICommand MapCommand { get; }
        public ICommand DashboardCommand { get; }
        public ICommand ReportsCommand { get; }
        public ICommand AssistantCommand { get; }
        public ICommand StaffCommand { get; }
        public ICommand AttendanceCommand { get; }
        public ICommand ReaderCommand { get; }
        public ICommand LoansCommand { get; }
        public ICommand CounterCommand { get; }
        public ICommand RequestsCommand { get; }
        public ICommand NotificationsCommand { get; }
        public ICommand FinesCommand { get; }
        public ICommand PolicyCommand { get; }
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
            (ReportsCommand as AsyncCommand)?.Refresh();
            (AssistantCommand as AsyncCommand)?.Refresh();
            (AttendanceCommand as AsyncCommand)?.Refresh();
            (InventoryCommand as AsyncCommand)?.Refresh();
            (MapCommand as AsyncCommand)?.Refresh();
            (ReaderCommand as AsyncCommand)?.Refresh();
            (LoansCommand as AsyncCommand)?.Refresh();
            (CounterCommand as AsyncCommand)?.Refresh();
            (RequestsCommand as AsyncCommand)?.Refresh();
            (NotificationsCommand as AsyncCommand)?.Refresh();
            (FinesCommand as AsyncCommand)?.Refresh();
            (PolicyCommand as AsyncCommand)?.Refresh();

            // Reuse one BookManagementViewModel for all browsing/search/metadata/copies work.
            Books = new BookManagementViewModel(_token, Role);
            Books.BorrowBookRequested += book => { _ = OpenBorrowRequestFromBookAsync(book); };
            MapPage = new LibraryMapViewModel(_token);
            Books.RouteToBookRequested += book => { _ = NavigateMapAsync(book.BookId); };
            if (CanManage) Staff = new HRManagementViewModel(_token, Role);
            if (IsAdmin) PolicyPage = new LibraryPolicyViewModel(_token);
            if (IsAdmin) ReportsPage = new LibraryReportsViewModel(_token);
            ReaderPanel = new ReaderManagementViewModel(_token, Role);
            ReaderPanel.ReaderDetailRequested += (sender, args) =>
            {
                // Navigate to a NEW PAGE instead of a Popup/Window overlay.
                CurrentPage = new ReaderDetailPage(ReaderPanel, BackFromReaderDetailsCommand);
            };
            Loans = new BorrowReturnViewModel(_token, Role);
            CounterPage = new WalkInCounterPage(Loans);
            Loans.IdentityDetailRequested += username => { _ = OpenRequestReaderAsync(username, CounterPage); };
            AssistantPage = new BookAssistantViewModel(_token);
            // IMPORTANT: the floating UserControl was bound before login validation finished.
            // Notify the View to re-evaluate its DataContext (without this, GỬI stays disabled).
            Notify(nameof(AssistantPage));
            AssistantPage.BookRequested += id => { _ = OpenAssistantBookAsync(id); };
            AssistantPage.FindShelfRequested += id => { IsAssistantOpen = false; _ = NavigateMapAsync(id); };
            if (IsReader)
            {
                Notifications = new NotificationCenterViewModel(_token);
                Notifications.UnreadCountChanged += count =>
                {
                    NotificationsMenuText = count > 0 ? "THÔNG BÁO (" + count + ")" : "THÔNG BÁO";
                    Notify(nameof(HasUnreadNotifications));
                };
                // The bell's list binds to the VM created after the Window's first render.
                Notify(nameof(Notifications));
                Notify(nameof(HasUnreadNotifications));
            }
            FinePage = new FineManagementViewModel(_token, Role);
            Requests = new BorrowRequestViewModel(_token, Role);
            Requests.ReaderDetailRequested += username => { _ = OpenRequestReaderAsync(username, Requests); };
            Status = "Đã xác thực phiên: " + Username + " (" + Role + ")";

            // Open the bookstore-style home page immediately after login.
            await NavigateHomeAsync();
            // Populate unread badge on login; no polling or background service.
            if (IsReader && Notifications != null)
            {
                await Notifications.LoadAsync();
                Notifications.StartAutoRefresh();
            }
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

        private async Task NavigateInventoryAsync()
        {
            if (!CanManage || Books == null) return;
            CurrentPage = new BookInventoryPage(Books);
            await Books.InitializeAsync();
            await Books.RefreshInventoryAsync();
        }

        private async Task NavigateMapAsync(long bookId = 0)
        {
            if (!IsAuthenticated || MapPage == null) return;
            CurrentPage = MapPage;
            await MapPage.LoadAsync(bookId);
        }

        private async Task NavigateDashboardAsync()
        {
            if (!IsAuthenticated) return;
            CurrentPage = Dashboard;
            await Dashboard.LoadAsync();
        }

        // Floating chat: the XAML shows EITHER the round AI launcher OR the panel.
        // The current page and chat history remain intact when toggling.
        private Task NavigateAssistantAsync()
        {
            if (!IsAuthenticated || AssistantPage == null) return Task.CompletedTask;
            IsAssistantOpen = !IsAssistantOpen;
            if (IsAssistantOpen) IsNotificationOpen = false;
            return Task.CompletedTask;
        }

        private async Task OpenAssistantBookAsync(long bookId)
        {
            if (!IsAuthenticated || Books == null) return;
            IsAssistantOpen = false;
            await NavigateHomeAsync();
            // Use the existing detail popup + rating + request flow on Home.
            var book = Books.Books.FirstOrDefault(b => b.BookId == bookId);
            if (book == null && AssistantPage != null)
            {
                // Home might have an active filter. Re-search by the exact grounded title.
                var suggestion = AssistantPage.Suggestions.FirstOrDefault(b => b.BookId == bookId);
                if (suggestion != null)
                {
                    Books.Query = suggestion.Title;
                    await Books.SearchAsync();
                    book = Books.Books.FirstOrDefault(b => b.BookId == bookId);
                }
            }
            if (book != null) Books.OpenBookCommand.Execute(book);
            else Status = "Không tìm thấy sách trong danh sách hiện tại. Hãy tải lại Trang chủ.";
        }

        private async Task NavigateReportsAsync()
        {
            if (!IsAdmin || ReportsPage == null) return;
            CurrentPage = ReportsPage;
            await ReportsPage.LoadAsync();
        }

        private async Task NavigateStaffAsync()
        {
            if (!IsAdmin || Staff == null) return;
            CurrentPage = Staff;
            await Staff.LoadAsync();
        }

        private async Task NavigateAttendanceAsync()
        {
            if (!CanManage || Staff == null) return;
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

        private async Task NavigateNotificationsAsync()
        {
            if (!IsReader || Notifications == null) return;
            IsNotificationOpen = !IsNotificationOpen;
            if (IsNotificationOpen)
            {
                IsAssistantOpen = false;
                await Notifications.LoadAsync();
            }
        }

        private async Task NavigatePolicyAsync()
        {
            if (!IsAdmin || PolicyPage == null) return;
            CurrentPage = PolicyPage;
            await PolicyPage.LoadAsync();
        }

        private async Task NavigateFinesAsync()
        {
            if (!IsAuthenticated || FinePage == null) return;
            CurrentPage = FinePage;
            await FinePage.LoadAsync();
        }

        private async Task LogoutAsync()
        {
            if (_loggingOut) return;
            _loggingOut = true;
            Notifications?.StopAutoRefresh();
            try { await _auth.LogoutAsync(_token); }
            finally { LogoutRequested?.Invoke(this, EventArgs.Empty); }
        }
    }
}

namespace QuanLyThuVien.ViewModels
{
    // Thin navigation wrapper: reuse BookManagementViewModel, no duplicate inventory VM.
    public sealed class BookInventoryPage
    {
        public BookManagementViewModel Books { get; private set; }
        public BookInventoryPage(BookManagementViewModel books) { Books = books; }
    }
}
