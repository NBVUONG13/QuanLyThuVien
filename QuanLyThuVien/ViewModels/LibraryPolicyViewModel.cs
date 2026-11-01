using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Input;
using QuanLyThuVien.Services;

namespace QuanLyThuVien.ViewModels
{
    public sealed class LibraryPolicyViewModel : MvvmBase
    {
        private readonly LibraryPolicyApiClient _api = new LibraryPolicyApiClient();
        private readonly string _token;
        private bool _busy, _loaded;
        private string _message = "Mở trang để tải chính sách đang có hiệu lực.";
        private string _version = "Chưa có", _maxRenewals = "2", _renewalDays = "7",
                       _fee = "2000", _grace = "0", _cap = "100000", _notes = "";
        private bool _allowLateRequest, _allowLateApproval;
        private long _policyId;

        public LibraryPolicyViewModel(string token)
        {
            _token = token;
            ReloadCommand = new AsyncCommand(_ => LoadAsync(), _ => !Busy);
            SaveCommand = new AsyncCommand(_ => SaveAsync(), _ => !Busy && _loaded);
        }

        public ObservableCollection<LibraryPolicyItem> History { get; } = new ObservableCollection<LibraryPolicyItem>();
        public ICommand ReloadCommand { get; }
        public ICommand SaveCommand { get; }
        public bool Busy
        {
            get => _busy;
            private set
            {
                if (Set(ref _busy, value))
                {
                    Notify(nameof(CanEdit));
                    (ReloadCommand as AsyncCommand)?.Refresh();
                    (SaveCommand as AsyncCommand)?.Refresh();
                }
            }
        }
        public bool CanEdit => !Busy && _loaded;
        public string Message { get => _message; private set => Set(ref _message, value); }
        public string CurrentVersion { get => _version; private set => Set(ref _version, value); }
        public string MaxRenewals { get => _maxRenewals; set => Set(ref _maxRenewals, value); }
        public string RenewalDays { get => _renewalDays; set => Set(ref _renewalDays, value); }
        public string OverdueFeePerDay { get => _fee; set => Set(ref _fee, value); }
        public string OverdueGraceDays { get => _grace; set => Set(ref _grace, value); }
        public string MaxOverdueFeePerDetail { get => _cap; set => Set(ref _cap, value); }
        public string Notes { get => _notes; set => Set(ref _notes, value); }
        public bool AllowOverdueRenewalRequests { get => _allowLateRequest; set => Set(ref _allowLateRequest, value); }
        public bool AllowOverdueRenewalApproval { get => _allowLateApproval; set => Set(ref _allowLateApproval, value); }

        public async Task LoadAsync()
        {
            if (Busy) return;
            Busy = true;
            try
            {
                var result = await _api.LoadAsync(_token);
                if (!result.IsSuccess)
                {
                    _loaded = false;
                    Message = result.Message;
                    return;
                }
                History.Clear();
                foreach (var row in result.History) History.Add(row);
                var current = result.Current;
                _policyId = current == null ? 0 : current.PolicyVersionId;
                CurrentVersion = current == null ? "Chưa có chính sách — tạo lần đầu" :
                    "Phiên bản #" + current.PolicyVersionId + " · hiệu lực: " + current.EffectiveAt;
                if (current != null)
                {
                    MaxRenewals = current.MaxRenewals.ToString(CultureInfo.InvariantCulture);
                    RenewalDays = current.RenewalDays.ToString(CultureInfo.InvariantCulture);
                    OverdueFeePerDay = current.OverdueFeePerDay.ToString("0.##", CultureInfo.InvariantCulture);
                    OverdueGraceDays = current.OverdueGraceDays.ToString(CultureInfo.InvariantCulture);
                    MaxOverdueFeePerDetail = current.MaxOverdueFeePerDetail.ToString("0.##", CultureInfo.InvariantCulture);
                    AllowOverdueRenewalRequests = current.AllowOverdueRenewalRequests;
                    AllowOverdueRenewalApproval = current.AllowOverdueRenewalApproval;
                    Notes = current.Notes ?? "";
                }
                _loaded = true;
                Message = "Đã tải chính sách. Chỉnh thông số rồi lưu sẽ tạo một phiên bản mới; không sửa bản cũ.";
            }
            finally { Busy = false; }
        }

        private async Task SaveAsync()
        {
            if (Busy || !_loaded) return;
            int renewals, days, grace;
            decimal fee, cap;
            if (!int.TryParse(MaxRenewals, NumberStyles.None, CultureInfo.InvariantCulture, out renewals) ||
                !int.TryParse(RenewalDays, NumberStyles.None, CultureInfo.InvariantCulture, out days) ||
                !int.TryParse(OverdueGraceDays, NumberStyles.None, CultureInfo.InvariantCulture, out grace) ||
                !decimal.TryParse(OverdueFeePerDay, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out fee) ||
                !decimal.TryParse(MaxOverdueFeePerDetail, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out cap) ||
                renewals < 0 || renewals > 10 || days < 1 || days > 365 || grace < 0 || grace > 365 ||
                fee < 0 || fee > 1000000m || cap < 0 || cap > 100000000m || (Notes ?? "").Length > 500)
            {
                Message = "Giá trị không hợp lệ. Lượt gia hạn 0–10; ngày gia hạn 1–365; ân hạn 0–365; tiền không âm. Nhập tiền không dùng dấu phân cách hàng nghìn.";
                return;
            }
            Busy = true;
            try
            {
                var request = new LibraryPolicyEdit
                {
                    ExpectedPolicyVersionId = _policyId,
                    MaxRenewals = renewals,
                    RenewalDays = days,
                    AllowOverdueRenewalRequests = AllowOverdueRenewalRequests,
                    AllowOverdueRenewalApproval = AllowOverdueRenewalApproval,
                    OverdueFeePerDay = fee,
                    OverdueGraceDays = grace,
                    MaxOverdueFeePerDetail = cap,
                    Notes = Notes == null ? "" : Notes.Trim()
                };
                // Explicitly avoid assuming success from a string response.
                var result = await _api.SaveWithStatusAsync(_token, request);
                Message = result.Message;
                if (result.IsSuccess)
                {
                    Busy = false;
                    await LoadAsync();
                    Message = result.Message;
                }
            }
            finally { Busy = false; }
        }
    }
}
