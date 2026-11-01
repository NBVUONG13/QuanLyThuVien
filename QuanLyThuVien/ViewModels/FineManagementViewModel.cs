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
    public sealed class FineManagementViewModel : MvvmBase
    {
        private readonly LoanApiClient _api = new LoanApiClient();
        private readonly string _token;
        private readonly bool _canManage;
        private bool _busy;
        private string _query = "", _message = "Nhấn LÀM MỚI để xem các khoản phạt.",
            _paymentAmount = "", _paymentMethod = "cash", _reference = "";
        private LibraryFine _selected;
        public FineManagementViewModel(string token, string role)
        {
            _token = token;
            _canManage = role == "Staff" || role == "Admin";
            LoadCommand = new AsyncCommand(_ => LoadAsync(), _ => !Busy);
            LoadPaymentsCommand = new AsyncCommand(_ => LoadPaymentsAsync(), _ => !Busy && Selected != null);
            PayCommand = new AsyncCommand(_ => PayAsync(), _ => CanManage && !Busy && Selected != null && Selected.Outstanding > 0);
        }
        public bool CanManage => _canManage;
        public ObservableCollection<LibraryFine> Items { get; } = new ObservableCollection<LibraryFine>();
        public ObservableCollection<LibraryFinePayment> Payments { get; } = new ObservableCollection<LibraryFinePayment>();
        public ICommand LoadCommand { get; }
        public ICommand LoadPaymentsCommand { get; }
        public ICommand PayCommand { get; }
        public bool Busy
        {
            get => _busy;
            private set { if (Set(ref _busy, value)) RefreshCommands(); }
        }
        public string Query { get => _query; set => Set(ref _query, value); }
        public string Message { get => _message; private set => Set(ref _message, value); }
        public string PaymentAmount { get => _paymentAmount; set => Set(ref _paymentAmount, value); }
        public string PaymentMethod { get => _paymentMethod; set => Set(ref _paymentMethod, value); }
        public string Reference { get => _reference; set => Set(ref _reference, value); }
        public LibraryFine Selected
        {
            get => _selected;
            set
            {
                if (!Set(ref _selected, value)) return;
                Payments.Clear();
                PaymentAmount = value != null && value.Outstanding > 0 ? value.Outstanding.ToString("0") : "";
                RefreshCommands();
            }
        }
        public decimal TotalOutstanding => Items.Sum(x => x.Outstanding);
        public string Summary => "Đang hiển thị " + Items.Count + " khoản phạt · Tổng còn nợ: " +
            TotalOutstanding.ToString("N0") + " VND";
        private void RefreshCommands()
        {
            (LoadCommand as AsyncCommand)?.Refresh();
            (LoadPaymentsCommand as AsyncCommand)?.Refresh();
            (PayCommand as AsyncCommand)?.Refresh();
        }
        public async Task LoadAsync()
        {
            if (Busy) return;
            Busy = true;
            try
            {
                var result = await _api.GetFinesAsync(_token, CanManage ? Query : "");
                if (!result.IsSuccess) { Message = result.Message; return; }
                Items.Clear();
                foreach (var fine in result.Fines) Items.Add(fine);
                Selected = null;
                Notify(nameof(TotalOutstanding));
                Notify(nameof(Summary));
                Message = Items.Count == 0 ? "Chưa có khoản phạt được ghi nhận."
                    : "Đã tải các khoản phạt. Chọn một dòng để xem lịch sử thu.";
            }
            finally { Busy = false; }
        }
        private async Task LoadPaymentsAsync()
        {
            if (Busy || Selected == null) return;
            long id = Selected.FineId;
            Busy = true;
            try
            {
                var result = await _api.GetFinePaymentsAsync(_token, id);
                if (!result.IsSuccess) { Message = result.Message; return; }
                Payments.Clear();
                foreach (var payment in result.Payments) Payments.Add(payment);
                Message = "Có " + Payments.Count + " lần thanh toán được ghi nhận cho khoản phạt #" + id + ".";
            }
            finally { Busy = false; }
        }
        private async Task PayAsync()
        {
            if (!CanManage || Busy || Selected == null || Selected.Outstanding <= 0) return;
            string raw = (PaymentAmount ?? "").Trim();
            if (raw.Length == 0 || raw.Any(c => c < '0' || c > '9') ||
                !decimal.TryParse(raw, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out decimal amount) ||
                amount <= 0 || amount > Selected.Outstanding)
            { Message = "Nhập số tiền VND nguyên dương, không dấu phân cách và không vượt số dư."; return; }
            if (PaymentMethod == "bank_transfer" && string.IsNullOrWhiteSpace(Reference))
            { Message = "Thanh toán chuyển khoản cần mã tham chiếu giao dịch."; return; }
            if (Reference != null && Reference.Trim().Length > 120)
            { Message = "Mã tham chiếu tối đa 120 ký tự."; return; }
            long fineId = Selected.FineId;
            string successMessage = "";
            Busy = true;
            try
            {
                var result = await _api.PayFineAsync(_token, fineId, amount, PaymentMethod, Reference ?? "");
                Message = result.Message;
                if (!result.IsSuccess) return;
                successMessage = result.Message;
            }
            finally { Busy = false; }
            // Reload balance from backend, never trust local math after concurrent payments.
            await LoadAsync();
            var same = Items.FirstOrDefault(x => x.FineId == fineId);
            if (same != null)
            {
                Selected = same;
                await LoadPaymentsAsync();
            }
            Message = successMessage;
        }
    }
}
