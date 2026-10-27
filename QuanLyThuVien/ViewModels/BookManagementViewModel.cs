using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using QuanLyThuVien.Services;

namespace QuanLyThuVien.ViewModels
{
    // One VM owns book catalog, search, book details, physical copies and reference metadata.
    // SearchViewModel derives from it so the search screen shares all logic, not a copy.
    public class BookManagementViewModel : MvvmBase
    {
        private readonly string _token;
        private readonly BookCatalogApiClient _catalog = new BookCatalogApiClient();
        private readonly BookDetailApiClient _detailApi = new BookDetailApiClient();
        private readonly ReferenceCatalogApiClient _references = new ReferenceCatalogApiClient();
        private string _query = "", _message = "", _copyCode = "", _shelf = "";
        private string _authorName = "", _authorDetail = "", _publisherName = "", _publisherDetail = "", _categoryName = "";
        private CatalogBook _selectedBook;
        private BookDetailResult _detail;
        private bool _busy;
        private int _activeTab;
        public int ActiveTab { get => _activeTab; set => Set(ref _activeTab, value); }
        public bool CanManage { get; }
        public ObservableCollection<CatalogBook> Books { get; } = new ObservableCollection<CatalogBook>();
        public ObservableCollection<BookCopyDto> Copies { get; } = new ObservableCollection<BookCopyDto>();
        public ObservableCollection<ReferenceEntry> Authors { get; } = new ObservableCollection<ReferenceEntry>();
        public ObservableCollection<ReferenceEntry> Publishers { get; } = new ObservableCollection<ReferenceEntry>();
        public ObservableCollection<ReferenceEntry> Categories { get; } = new ObservableCollection<ReferenceEntry>();

        public BookManagementViewModel(string token, string role)
        {
            _token = token;
            CanManage = role == "Admin" || role == "Staff";
            SearchCommand = new AsyncCommand(_ => SearchAsync());
            DetailCommand = new AsyncCommand(_ => LoadDetailAsync(), _ => SelectedBook != null);
            AddCopyCommand = new AsyncCommand(_ => AddCopyAsync(), _ => CanManage && SelectedBook != null);
            LoadReferencesCommand = new AsyncCommand(_ => LoadReferencesAsync(), _ => CanManage);
            AddAuthorCommand = new AsyncCommand(_ => AddReferenceAsync("authors", AuthorName, AuthorDetail), _ => CanManage);
            AddPublisherCommand = new AsyncCommand(_ => AddReferenceAsync("publishers", PublisherName, PublisherDetail), _ => CanManage);
            AddCategoryCommand = new AsyncCommand(_ => AddReferenceAsync("categories", CategoryName, ""), _ => CanManage);
        }
        public string Query { get => _query; set => Set(ref _query, value); }
        public string Message { get => _message; private set => Set(ref _message, value); }
        public bool Busy { get => _busy; private set => Set(ref _busy, value); }
        public string CopyCode { get => _copyCode; set => Set(ref _copyCode, value); }
        public string Shelf { get => _shelf; set => Set(ref _shelf, value); }
        public string AuthorName { get => _authorName; set => Set(ref _authorName, value); }
        public string AuthorDetail { get => _authorDetail; set => Set(ref _authorDetail, value); }
        public string PublisherName { get => _publisherName; set => Set(ref _publisherName, value); }
        public string PublisherDetail { get => _publisherDetail; set => Set(ref _publisherDetail, value); }
        public string CategoryName { get => _categoryName; set => Set(ref _categoryName, value); }
        public CatalogBook SelectedBook
        {
            get => _selectedBook;
            set
            {
                if (!Set(ref _selectedBook, value)) return;
                Detail = null;
                Copies.Clear();
                (DetailCommand as AsyncCommand)?.Refresh();
                (AddCopyCommand as AsyncCommand)?.Refresh();
            }
        }
        public BookDetailResult Detail { get => _detail; private set => Set(ref _detail, value); }
        public ICommand SearchCommand { get; }
        public ICommand DetailCommand { get; }
        public ICommand AddCopyCommand { get; }
        public ICommand LoadReferencesCommand { get; }
        public ICommand AddAuthorCommand { get; }
        public ICommand AddPublisherCommand { get; }
        public ICommand AddCategoryCommand { get; }
        private static void Replace<T>(ObservableCollection<T> dest, IEnumerable<T> source)
        {
            dest.Clear();
            if (source != null) foreach (var row in source) dest.Add(row);
        }
        public async Task SearchAsync()
        {
            if (Busy) return;
            Busy = true; Message = "Đang tải sách...";
            try
            {
                var result = await _catalog.SearchAsync(_token, Query);
                if (!result.IsSuccess) { Message = result.Message; return; }
                Replace(Books, result.Books);
                SelectedBook = null;
                Message = "Tìm thấy " + Books.Count + " đầu sách (tối đa 100).";
            }
            catch (Exception) { Message = "Lỗi khi tải sách từ API."; }
            finally { Busy = false; }
        }
        public async Task LoadDetailAsync()
        {
            if (Busy || SelectedBook == null) return;
            Busy = true; Message = "Đang tải chi tiết...";
            try
            {
                long id = SelectedBook.BookId;
                var details = await _detailApi.GetDetailsAsync(_token, id);
                if (!string.IsNullOrEmpty(details.Error)) { Message = details.Error; return; }
                if (SelectedBook == null || SelectedBook.BookId != id) return;
                Detail = details;
                if (CanManage) await ReloadCopiesAsync(id);
                Message = "Đã tải chi tiết đầu sách.";
            }
            catch (Exception) { Message = "Không tải được chi tiết sách."; }
            finally { Busy = false; }
        }
        private async Task ReloadCopiesAsync(long id)
        {
            var result = await _detailApi.GetCopiesAsync(_token, id);
            if (!string.IsNullOrWhiteSpace(result.Error)) { Message = result.Error; return; }
            if (SelectedBook != null && SelectedBook.BookId == id) Replace(Copies, result.Copies);
        }
        public async Task AddCopyAsync()
        {
            if (Busy || !CanManage || SelectedBook == null) return;
            if (string.IsNullOrWhiteSpace(CopyCode)) { Message = "Nhập mã cuốn trước."; return; }
            Busy = true;
            try
            {
                long id = SelectedBook.BookId;
                string message = await _detailApi.AddCopyAsync(_token, id, CopyCode.Trim(), (Shelf ?? "").Trim());
                Message = message;
                if (SelectedBook != null && SelectedBook.BookId == id)
                {
                    await ReloadCopiesAsync(id);
                    var detail = await _detailApi.GetDetailsAsync(_token, id);
                    if (string.IsNullOrEmpty(detail.Error)) Detail = detail;
                }
            }
            catch (Exception) { Message = "Không thêm được bản sao sách."; }
            finally { Busy = false; }
        }
        public async Task LoadReferencesAsync()
        {
            if (!CanManage) return;
            Message = "Đang tải tác giả / nhà xuất bản / thể loại...";
            try
            {
                var x = await _references.GetAsync(_token);
                if (!x.IsSuccess) { Message = x.Message; return; }
                Replace(Authors, x.Authors); Replace(Publishers, x.Publishers); Replace(Categories, x.Categories);
                Message = "Đã tải danh mục nền.";
            }
            catch (Exception) { Message = "Không tải được danh mục nền."; }
        }
        private async Task AddReferenceAsync(string kind, string name, string detail)
        {
            if (!CanManage) { Message = "Không có quyền quản lý danh mục."; return; }
            try
            {
                var result = await _references.AddAsync(_token, kind, name, detail);
                Message = result;
                if (result.StartsWith("Đã thêm:", StringComparison.Ordinal))
                {
                    await LoadReferencesAsync();
                    Message = result;
                    if (kind == "authors") AuthorName = "";
                    if (kind == "publishers") PublisherName = "";
                    if (kind == "categories") CategoryName = "";
                }
            }
            catch (Exception) { Message = "Không thêm được danh mục nền."; }
        }
    }
}
