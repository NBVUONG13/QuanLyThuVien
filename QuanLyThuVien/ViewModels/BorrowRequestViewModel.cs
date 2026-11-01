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
