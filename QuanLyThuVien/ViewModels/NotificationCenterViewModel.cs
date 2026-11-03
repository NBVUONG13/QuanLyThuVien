using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using QuanLyThuVien.Services;

namespace QuanLyThuVien.ViewModels
{
    public sealed class NotificationCenterViewModel : MvvmBase
    {
        private readonly LoanApiClient _api = new LoanApiClient();
        private readonly string _token;
        private string _message = "Nhấn LÀM MỚI để kiểm tra thông báo mới.";
        private long _unreadCount;
        private LibraryNotification _selected;
        private bool _loading;
        private DispatcherTimer _refreshTimer;
        public event Action<long> UnreadCountChanged;

        public NotificationCenterViewModel(string token)
        {
            _token = token;
            LoadCommand = new AsyncCommand(_ => LoadAsync());
            MarkReadCommand = new AsyncCommand(_ => MarkSelectedAsync(),
                _ => Selected != null && Selected.IsUnread && !_loading);
            MarkAllReadCommand = new AsyncCommand(_ => MarkAllAsync(),
                _ => UnreadCount > 0 && !_loading);
        }
        public ObservableCollection<LibraryNotification> Items { get; }
            = new ObservableCollection<LibraryNotification>();
        public LibraryNotification Selected
        {
            get => _selected;
            set { if (Set(ref _selected, value)) RefreshCommands(); }
        }
        public string Message { get => _message; private set => Set(ref _message, value); }
        public long UnreadCount
        {
            get => _unreadCount;
            private set
            {
                if (Set(ref _unreadCount, value))
                {
                    Notify(nameof(UnreadLabel));
                    UnreadCountChanged?.Invoke(value);
                    RefreshCommands();
                }
            }
        }
        public string UnreadLabel => "Chưa đọc: " + UnreadCount;
        public ICommand LoadCommand { get; }
        public ICommand MarkReadCommand { get; }
        public ICommand MarkAllReadCommand { get; }
        // Refresh on a timer while the Reader's main window is open.
        // UI dispatcher timer: never creates background workers or extra threads.
        public void StartAutoRefresh()
        {
            if (_refreshTimer != null) return;
            _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMinutes(2)
            };
            _refreshTimer.Tick += RefreshTimer_Tick;
            _refreshTimer.Start();
        }

        public void StopAutoRefresh()
        {
            if (_refreshTimer == null) return;
            _refreshTimer.Stop();
            _refreshTimer.Tick -= RefreshTimer_Tick;
            _refreshTimer = null;
        }

        private async void RefreshTimer_Tick(object sender, EventArgs args)
        {
            if (_loading) return;
            try { await LoadAsync(); }
            catch (Exception) { Message = "Làm mới thông báo thất bại. Sẽ thử lại sau."; }
        }

        private void RefreshCommands()
        {
            (MarkReadCommand as AsyncCommand)?.Refresh();
            (MarkAllReadCommand as AsyncCommand)?.Refresh();
        }
        public async Task LoadAsync()
        {
            if (_loading) return;
            _loading = true;
            RefreshCommands();
            try
            {
                // Sync first: only the server may create due reminders for this Reader.
                // Even if sync fails, load existing notifications and keep the bell usable.
                var sync = await _api.SyncDueRemindersAsync(_token);
                var result = await _api.GetNotificationsAsync(_token);
                if (!result.IsSuccess) { Message = result.Message; return; }
                Items.Clear();
                foreach (var n in result.Notifications) Items.Add(n);
                Selected = null;
                UnreadCount = result.UnreadCount;
                Message = !sync.IsSuccess
                    ? "Đã tải thông báo. Chưa kiểm tra được hạn trả mới (kiểm tra API bước 85)."
                    : Items.Count == 0 ? "Bạn chưa có thông báo nào."
                    : "Hiển thị " + Items.Count + " thông báo gần nhất (tối đa 100).";
            }
            finally { _loading = false; RefreshCommands(); }
        }
        private async Task MarkSelectedAsync()
        {
            if (_loading || Selected == null || !Selected.IsUnread) return;
            _loading = true;
            RefreshCommands();
            try
            {
                var result = await _api.MarkNotificationReadAsync(_token, Selected.NotificationId);
                if (!result.IsSuccess) { Message = result.Message; return; }
            }
            finally { _loading = false; RefreshCommands(); }
            await LoadAsync();
        }
        private async Task MarkAllAsync()
        {
            if (_loading || UnreadCount <= 0) return;
            _loading = true;
            RefreshCommands();
            try
            {
                var result = await _api.MarkAllNotificationsReadAsync(_token);
                if (!result.IsSuccess) { Message = result.Message; return; }
            }
            finally { _loading = false; RefreshCommands(); }
            await LoadAsync();
        }
    }
}
