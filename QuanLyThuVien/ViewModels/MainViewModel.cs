using System;
using System.Threading.Tasks;
using System.Windows.Input;
using QuanLyThuVien.Services;

namespace QuanLyThuVien.ViewModels
{
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
            DashboardCommand = new AsyncCommand(_ => NavigateDashboardAsync());
            BooksCommand = new AsyncCommand(_ => NavigateBooksAsync());
            SearchCommand = new AsyncCommand(_ => NavigateSearchAsync());
            StaffCommand = new AsyncCommand(_ => NavigateStaffAsync(), _ => IsAdmin);
            ReferencesCommand = new AsyncCommand(_ => NavigateReferencesAsync(), _ => CanManage);
            LogoutCommand = new AsyncCommand(_ => LogoutAsync());
            PendingCommand = new ActionCommand(p => { if (IsAuthenticated) CurrentPage = "Chức năng '" + p + "' chưa triển khai."; });
        }
        public DashboardViewModel Dashboard { get; }
        public BookManagementViewModel Books { get; private set; }
        public SearchViewModel Search { get; private set; }
        public HRManagementViewModel Staff { get; private set; }
        public object CurrentPage { get => _currentPage; private set => Set(ref _currentPage, value); }
        public string Username { get => _username; private set => Set(ref _username, value); }
        public string Role { get => _role; private set => Set(ref _role, value); }
        public string Status { get => _status; private set => Set(ref _status, value); }
        public bool IsAuthenticated { get => _authenticated; private set => Set(ref _authenticated, value); }
        public bool IsReader => IsAuthenticated && Role == "Reader";
        public bool IsAdmin => IsAuthenticated && Role == "Admin";
        public bool CanManage => IsAuthenticated && (Role == "Staff" || Role == "Admin");
        public ICommand DashboardCommand { get; }
        public ICommand BooksCommand { get; }
        public ICommand SearchCommand { get; }
        public ICommand StaffCommand { get; }
        public ICommand ReferencesCommand { get; }
        public ICommand LogoutCommand { get; }
        public ICommand PendingCommand { get; }
        public async Task InitializeAsync()
        {
            var result = await _auth.ValidateAsync(_token);
            if (!result.IsSuccess || (result.Role != "Admin" && result.Role != "Staff" && result.Role != "Reader"))
            {
                Status = "Phiên đăng nhập không hợp lệ. Vui lòng đăng nhập lại.";
                LogoutRequested?.Invoke(this, EventArgs.Empty);
                return;
            }
            Username = result.Username;
            Role = result.Role;
            IsAuthenticated = true;
            Notify(nameof(IsReader)); Notify(nameof(IsAdmin)); Notify(nameof(CanManage));
            (StaffCommand as AsyncCommand)?.Refresh();
            (ReferencesCommand as AsyncCommand)?.Refresh();
            Books = new BookManagementViewModel(_token, Role);
            Search = new SearchViewModel(_token, Role);
            if (IsAdmin) Staff = new HRManagementViewModel(_token);
            Status = "Đã xác thực phiên: " + Username + " (" + Role + ")";
            await NavigateDashboardAsync();
        }
        private async Task NavigateDashboardAsync()
        {
            if (!IsAuthenticated) return;
            CurrentPage = Dashboard;
            await Dashboard.LoadAsync();
        }
        private async Task NavigateBooksAsync()
        {
            if (!IsAuthenticated) return;
            Books.ActiveTab = 0;
            CurrentPage = Books;
            await Books.SearchAsync();
        }
        private async Task NavigateSearchAsync()
        {
            if (!IsAuthenticated) return;
            CurrentPage = Search;
            await Search.SearchAsync();
        }
        private async Task NavigateReferencesAsync()
        {
            if (!CanManage) return;
            Books.ActiveTab = 1;
            CurrentPage = Books;
            await Books.LoadReferencesAsync();
        }
        private async Task NavigateStaffAsync()
        {
            if (!IsAdmin) return;
            CurrentPage = Staff;
            await Staff.LoadAsync();
        }
        private async Task LogoutAsync()
        {
            if (_loggingOut) return;
            _loggingOut = true;
            try { await _auth.LogoutAsync(_token); }
            finally { LogoutRequested?.Invoke(this, EventArgs.Empty); }
        }
    }
}
