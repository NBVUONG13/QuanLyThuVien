using System;
using System.Threading.Tasks;
using System.Windows.Input;
using QuanLyThuVien.Services;

namespace QuanLyThuVien.ViewModels
{
    public sealed class DashboardViewModel : MvvmBase
    {
        private readonly DashboardApiClient _api = new DashboardApiClient();
        private readonly string _token;
        private string _status = "Chưa tải dữ liệu.";
        private long _books, _authors, _categories, _copies, _readers;
        public DashboardViewModel(string token)
        {
            _token = token;
            RefreshCommand = new AsyncCommand(_ => LoadAsync());
        }
        public string Status { get => _status; private set => Set(ref _status, value); }
        public long Books { get => _books; private set => Set(ref _books, value); }
        public long Authors { get => _authors; private set => Set(ref _authors, value); }
        public long Categories { get => _categories; private set => Set(ref _categories, value); }
        public long Copies { get => _copies; private set => Set(ref _copies, value); }
        public long Readers { get => _readers; private set => Set(ref _readers, value); }
        public ICommand RefreshCommand { get; }
        public async Task LoadAsync()
        {
            Status = "Đang tải thống kê...";
            try
            {
                var r = await _api.LoadAsync(_token);
                if (!r.IsSuccess) { Status = r.Message; return; }
                Books = r.Books; Authors = r.Authors; Categories = r.Categories;
                Copies = r.PhysicalCopies; Readers = r.Readers;
                Status = "Dữ liệu trực tiếp từ Aiven PostgreSQL.";
            }
            catch (Exception) { Status = "Không tải được Dashboard. Kiểm tra API."; }
        }
    }
}
