using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using QuanLyThuVien.Services;

namespace QuanLyThuVien.ViewModels
{
    public sealed class HRManagementViewModel : MvvmBase
    {
        private readonly StaffAdminApiClient _api = new StaffAdminApiClient();
        private readonly string _token;
        private string _username = "", _name = "", _code = "", _status = "";
        private bool _busy;
        public HRManagementViewModel(string token)
        {
            _token = token;
            RefreshCommand = new AsyncCommand(_ => LoadAsync());
            CreateCommand = new AsyncCommand(p => CreateAsync(p as string));
        }
        public ObservableCollection<StaffItem> Staff { get; } = new ObservableCollection<StaffItem>();
        public string Username { get => _username; set => Set(ref _username, value); }
        public string FullName { get => _name; set => Set(ref _name, value); }
        public string StaffCode { get => _code; set => Set(ref _code, value); }
        public string Status { get => _status; private set => Set(ref _status, value); }
        public bool Busy { get => _busy; private set => Set(ref _busy, value); }
        public ICommand RefreshCommand { get; }
        public ICommand CreateCommand { get; }
        public async Task LoadAsync()
        {
            Status = "Đang tải nhân viên...";
            try
            {
                var result = await _api.ListAsync(_token);
                if (!result.IsSuccess) { Status = result.Message; return; }
                Replace(result.Staff);
                Status = "Đã tải " + Staff.Count + " tài khoản nhân viên.";
            }
            catch (Exception) { Status = "Không tải được nhân viên."; }
        }
        private void Replace(IEnumerable<StaffItem> data)
        {
            Staff.Clear();
            if (data != null) foreach (var item in data) Staff.Add(item);
        }
        private async Task CreateAsync(string password)
        {
            if (Busy) return;
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
                        Replace(list.Staff);
                        Username = ""; FullName = ""; StaffCode = "";
                        Status = result + " Đã đồng bộ danh sách: " + Staff.Count + " tài khoản.";
                    }
                    else Status = result + " Nhưng tải lại danh sách thất bại: " + list.Message;
                }
            }
            catch (Exception) { Status = "Không tạo được Staff. Kiểm tra API."; }
            finally { Busy = false; }
        }
    }
}
