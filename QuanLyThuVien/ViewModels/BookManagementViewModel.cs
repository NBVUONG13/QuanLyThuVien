using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using QuanLyThuVien.Services;

namespace QuanLyThuVien.ViewModels
{
    // One ViewModel for the EXISTING BookManagementView: catalog, search, dropdown filters,
    // book details, physical copies and Staff/Admin reference management.
    // Never call WPF controls directly; all displayed lists are ObservableCollection<T>.
    public class BookManagementViewModel : MvvmBase
    {
        private readonly string _token;
        private readonly BookCatalogApiClient _catalog = new BookCatalogApiClient();
        private readonly BookDetailApiClient _detailApi = new BookDetailApiClient();
        private readonly ReferenceCatalogApiClient _references = new ReferenceCatalogApiClient();
        private readonly List<CatalogBook> _searchResults = new List<CatalogBook>();

        private string _query = "", _message = "", _copyCode = "", _shelf = "";
        private string _authorName = "", _authorDetail = "", _publisherName = "", _publisherDetail = "", _categoryName = "";
        private CatalogBook _selectedBook;
        private BookDetailResult _detail;
        private const string All = "Tất cả";
        private const string None = "Không có";
        private string _selectedAuthor = All, _selectedPublisher = All;
        private string _selectedCategory = All, _selectedYear = All;
        private bool _updatingFacets;
        private string _detailMessage = "";
        private bool _busy, _booksLoaded, _referencesLoaded, _loadingReferences, _detailExpanded;
        private enum Facet { None, Category, Author, Publisher, Year }

        public BookManagementViewModel(string token, string role)
        {
            _token = token;
            CanManage = role == "Admin" || role == "Staff";
            SearchCommand = new AsyncCommand(_ => SearchAsync());
            ResetFiltersCommand = new AsyncCommand(async _ =>
            {
                _updatingFacets = true;
                try
                {
                    SelectedAuthor = All;
                    SelectedPublisher = All;
                    SelectedCategory = All;
                    SelectedYear = All;
                    Query = "";
                }
                finally { _updatingFacets = false; }
                await SearchAsync();
            });
            OpenBookCommand = new AsyncCommand(async book =>
            {
                if (!(book is CatalogBook selected)) return;
                SelectedBook = selected;
                await LoadDetailAsync();
            });
            DetailCommand = new AsyncCommand(_ => LoadDetailAsync(), _ => SelectedBook != null);
            CloseDetailCommand = new ActionCommand(_ => DetailExpanded = false);
            AddCopyCommand = new AsyncCommand(_ => AddCopyAsync(), _ => CanManage && SelectedBook != null);
            LoadReferencesCommand = new AsyncCommand(_ => LoadReferencesAsync(), _ => !_loadingReferences);
            AddAuthorCommand = new AsyncCommand(_ => AddReferenceAsync("authors", AuthorName, AuthorDetail), _ => CanManage);
            AddPublisherCommand = new AsyncCommand(_ => AddReferenceAsync("publishers", PublisherName, PublisherDetail), _ => CanManage);
            AddCategoryCommand = new AsyncCommand(_ => AddReferenceAsync("categories", CategoryName, ""), _ => CanManage);
        }

        public bool CanManage { get; }
        public ObservableCollection<CatalogBook> Books { get; } = new ObservableCollection<CatalogBook>();
        public ObservableCollection<BookCopyDto> Copies { get; } = new ObservableCollection<BookCopyDto>();
        public ObservableCollection<ReferenceEntry> Authors { get; } = new ObservableCollection<ReferenceEntry>();
        public ObservableCollection<ReferenceEntry> Publishers { get; } = new ObservableCollection<ReferenceEntry>();
        public ObservableCollection<ReferenceEntry> Categories { get; } = new ObservableCollection<ReferenceEntry>();
        // Faceted options come from books (real book-author/category/publisher links),
        // not from unrelated reference entries that might contain zero books.
        public ObservableCollection<string> CategoryOptions { get; } = new ObservableCollection<string> { All };
        public ObservableCollection<string> AuthorOptions { get; } = new ObservableCollection<string> { All };
        public ObservableCollection<string> PublisherOptions { get; } = new ObservableCollection<string> { All };
        public ObservableCollection<string> YearOptions { get; } = new ObservableCollection<string> { All };

        public bool DetailExpanded { get => _detailExpanded; set => Set(ref _detailExpanded, value); }
        public string DetailMessage { get => _detailMessage; private set => Set(ref _detailMessage, value); }
        public bool HasNoBooks => _booksLoaded && Books.Count == 0;
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

        public string SelectedAuthor
        {
            get => _selectedAuthor;
            set { if (Set(ref _selectedAuthor, value ?? All)) Recalculate(Facet.Author); }
        }
        public string SelectedPublisher
        {
            get => _selectedPublisher;
            set { if (Set(ref _selectedPublisher, value ?? All)) Recalculate(Facet.Publisher); }
        }
        public string SelectedCategory
        {
            get => _selectedCategory;
            set { if (Set(ref _selectedCategory, value ?? All)) Recalculate(Facet.Category); }
        }
        public string SelectedYear
        {
            get => _selectedYear;
            set { if (Set(ref _selectedYear, value ?? All)) Recalculate(Facet.Year); }
        }

        public CatalogBook SelectedBook
        {
            get => _selectedBook;
            set
            {
                if (!Set(ref _selectedBook, value)) return;
                Detail = null;
                DetailExpanded = false;
                DetailMessage = "";
                Copies.Clear();
                (DetailCommand as AsyncCommand)?.Refresh();
                (AddCopyCommand as AsyncCommand)?.Refresh();
            }
        }
        public BookDetailResult Detail
        {
            get => _detail;
            private set
            {
                if (!Set(ref _detail, value)) return;
                Notify(nameof(DetailDescription));
                Notify(nameof(DetailAuthors));
                Notify(nameof(DetailCategories));
                Notify(nameof(DetailPublisher));
                Notify(nameof(DetailPublicationYear));
                Notify(nameof(DetailIsbn));
            }
        }
        public string DetailDescription => Display(Detail?.Description, "Chưa có mô tả.");
        public string DetailAuthors => Display(Detail?.Authors);
        public string DetailCategories => Display(Detail?.Categories);
        public string DetailPublisher => Display(Detail?.Publisher);
        public string DetailIsbn => Display(Detail?.Isbn);
        public string DetailPublicationYear => Detail == null || Detail.PublicationYear <= 0
            ? "Không có" : Detail.PublicationYear.ToString();
        private static string Display(string value, string fallback = "Không có") =>
            string.IsNullOrWhiteSpace(value) ? fallback : value;


        public ICommand SearchCommand { get; }
        public ICommand ResetFiltersCommand { get; }
        public ICommand OpenBookCommand { get; }
        public ICommand DetailCommand { get; }
        public ICommand CloseDetailCommand { get; }
        public ICommand AddCopyCommand { get; }
        public ICommand LoadReferencesCommand { get; }
        public ICommand AddAuthorCommand { get; }
        public ICommand AddPublisherCommand { get; }
        public ICommand AddCategoryCommand { get; }

        private static void Replace<T>(ObservableCollection<T> collection, IEnumerable<T> source)
        {
            collection.Clear();
            if (source != null)
                foreach (var item in source) collection.Add(item);
        }

        public async Task InitializeAsync()
        {
            // Entering Home loads both the books and the horizontal dropdown menus automatically.
            if (!_booksLoaded) await SearchAsync();
            if (!_referencesLoaded) await LoadReferencesAsync();
        }

        public async Task SearchAsync()
        {
            if (Busy) return;
            Busy = true;
            Message = "Đang tìm sách...";
            try
            {
                var result = await _catalog.SearchAsync(_token, Query);
                if (!result.IsSuccess) { Message = result.Message; return; }
                _searchResults.Clear();
                if (result.Books != null) _searchResults.AddRange(result.Books);
                _booksLoaded = true;
                Recalculate(Facet.None);
            }
            catch (Exception) { Message = "Không tải được danh mục sách từ API."; }
            finally { Busy = false; }
        }

        // Client-side faceted filtering over the currently loaded API results (max 100).
        // Changing a facet recalculates all other dropdown options. Existing compatible
        // choices are retained; incompatible older choices are cleared so the newly
        // chosen filter takes priority.
        private void Recalculate(Facet changed)
        {
            if (_updatingFacets) return;
            _updatingFacets = true;
            try
            {
                if (changed == Facet.None && !HasMatchingBook())
                {
                    // A different text search may invalidate previous choices.
                    foreach (Facet f in new[] { Facet.Category, Facet.Author, Facet.Publisher, Facet.Year })
                        SetFacet(f, All);
                }
                else if (changed != Facet.None && !HasMatchingBook())
                {
                    foreach (Facet f in new[] { Facet.Category, Facet.Author, Facet.Publisher, Facet.Year })
                    {
                        if (f == changed || GetFacet(f) == All) continue;
                        SetFacet(f, All);
                        if (HasMatchingBook()) break;
                    }
                }

                var cat = _selectedCategory;
                var author = _selectedAuthor;
                var publisher = _selectedPublisher;
                var year = _selectedYear;
                // Restore each selection immediately after that ComboBox's ItemsSource changes.
                // Otherwise WPF's transient null selection could corrupt later facet calculations.
                ReplaceOptions(CategoryOptions, GetAvailableOptions(Facet.Category));
                SetFacet(Facet.Category, CategoryOptions.Contains(cat) ? cat : All);
                ReplaceOptions(AuthorOptions, GetAvailableOptions(Facet.Author));
                SetFacet(Facet.Author, AuthorOptions.Contains(author) ? author : All);
                ReplaceOptions(PublisherOptions, GetAvailableOptions(Facet.Publisher));
                SetFacet(Facet.Publisher, PublisherOptions.Contains(publisher) ? publisher : All);
                ReplaceOptions(YearOptions, GetAvailableOptions(Facet.Year));
                SetFacet(Facet.Year, YearOptions.Contains(year) ? year : All);
                Notify(nameof(SelectedCategory));
                Notify(nameof(SelectedAuthor));
                Notify(nameof(SelectedPublisher));
                Notify(nameof(SelectedYear));

                Replace(Books, _searchResults.Where(b => Matches(b, Facet.None)));
                if (SelectedBook != null && !Books.Any(b => b.BookId == SelectedBook.BookId))
                    SelectedBook = null;
                Notify(nameof(HasNoBooks));
                Message = Books.Count == 0
                    ? "Không có sách phù hợp với bộ lọc hoặc từ khóa đã chọn."
                    : "Hiển thị " + Books.Count + " / " + _searchResults.Count + " đầu sách.";
            }
            finally { _updatingFacets = false; }
        }

        private void SetFacet(Facet facet, string value)
        {
            switch (facet)
            {
                case Facet.Category: Set(ref _selectedCategory, value, nameof(SelectedCategory)); break;
                case Facet.Author: Set(ref _selectedAuthor, value, nameof(SelectedAuthor)); break;
                case Facet.Publisher: Set(ref _selectedPublisher, value, nameof(SelectedPublisher)); break;
                case Facet.Year: Set(ref _selectedYear, value, nameof(SelectedYear)); break;
            }
        }
        private string GetFacet(Facet facet)
        {
            switch (facet)
            {
                case Facet.Category: return _selectedCategory;
                case Facet.Author: return _selectedAuthor;
                case Facet.Publisher: return _selectedPublisher;
                case Facet.Year: return _selectedYear;
                default: return All;
            }
        }
        private bool HasMatchingBook() => _searchResults.Any(b => Matches(b, Facet.None));
        private bool Matches(CatalogBook book, Facet ignored)
        {
            return (ignored == Facet.Category || _selectedCategory == All || CsvContains(book.Categories, _selectedCategory))
                && (ignored == Facet.Author || _selectedAuthor == All || CsvContains(book.Authors, _selectedAuthor))
                && (ignored == Facet.Publisher || _selectedPublisher == All ||
                    string.Equals(Normalize(book.Publisher), _selectedPublisher, StringComparison.OrdinalIgnoreCase))
                && (ignored == Facet.Year || _selectedYear == All ||
                    string.Equals(book.PublicationYear > 0 ? book.PublicationYear.ToString() : None,
                                  _selectedYear, StringComparison.Ordinal));
        }
        private static string Normalize(string value) => string.IsNullOrWhiteSpace(value) ? None : value.Trim();
        private static bool CsvContains(string values, string choice)
        {
            if (choice == None) return string.IsNullOrWhiteSpace(values);
            if (string.IsNullOrWhiteSpace(values)) return false;
            return values.Split(',').Any(part => string.Equals(part.Trim(), choice, StringComparison.OrdinalIgnoreCase));
        }
        private string[] GetAvailableOptions(Facet facet)
        {
            var values = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
            foreach (var book in _searchResults.Where(b => Matches(b, facet)))
            {
                switch (facet)
                {
                    case Facet.Category:
                        AddCommaSeparated(values, book.Categories);
                        break;
                    case Facet.Author:
                        AddCommaSeparated(values, book.Authors);
                        break;
                    case Facet.Publisher:
                        values.Add(Normalize(book.Publisher));
                        break;
                    case Facet.Year:
                        values.Add(book.PublicationYear > 0 ? book.PublicationYear.ToString() : None);
                        break;
                }
            }
            if (values.Count == 0) values.Add(None);
            return new[] { All }.Concat(values).ToArray();
        }
        private static void AddCommaSeparated(SortedSet<string> values, string csv)
        {
            if (string.IsNullOrWhiteSpace(csv)) { values.Add(None); return; }
            foreach (var part in csv.Split(','))
            {
                if (!string.IsNullOrWhiteSpace(part)) values.Add(part.Trim());
            }
        }
        private static void ReplaceOptions(ObservableCollection<string> target, IEnumerable<string> options)
        {
            var next = options.ToList();
            if (target.SequenceEqual(next)) return;
            target.Clear();
            foreach (string value in next) target.Add(value);
        }

        public async Task LoadReferencesAsync()
        {
            // Reader may browse categories/authors/publishers; only Staff/Admin may CREATE them.
            if (_loadingReferences) return;
            _loadingReferences = true;
            (LoadReferencesCommand as AsyncCommand)?.Refresh();
            try
            {
                var result = await _references.GetAsync(_token);
                if (!result.IsSuccess)
                {
                    Message = "Không tải được menu danh mục: " + result.Message;
                    return;
                }
                Replace(Authors, result.Authors);
                Replace(Publishers, result.Publishers);
                Replace(Categories, result.Categories);
                _referencesLoaded = true;
            }
            catch (Exception) { Message = "Không tải được menu thể loại/tác giả/NXB."; }
            finally
            {
                _loadingReferences = false;
                (LoadReferencesCommand as AsyncCommand)?.Refresh();
            }
        }

        public async Task LoadDetailAsync()
        {
            if (Busy || SelectedBook == null) return;
            long id = SelectedBook.BookId;
            DetailExpanded = true; // Show the centred temporary dialog immediately.
            DetailMessage = "Đang tải chi tiết sách...";
            Detail = null;
            Busy = true;
            try
            {
                var data = await _detailApi.GetDetailsAsync(_token, id);
                if (!string.IsNullOrWhiteSpace(data.Error))
                {
                    DetailMessage = data.Error;
                    return;
                }
                if (SelectedBook == null || SelectedBook.BookId != id) return;
                Detail = data;
                DetailMessage = "";
                if (CanManage) await ReloadCopiesAsync(id);
            }
            catch (Exception) { DetailMessage = "Không tải được chi tiết sách."; }
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
                Message = await _detailApi.AddCopyAsync(_token, id, CopyCode.Trim(), (Shelf ?? "").Trim());
                DetailMessage = Message;
                if (Message == "Đã thêm cuốn vật lý thành công.") CopyCode = "";
                if (SelectedBook != null && SelectedBook.BookId == id)
                {
                    await ReloadCopiesAsync(id);
                    var detail = await _detailApi.GetDetailsAsync(_token, id);
                    if (string.IsNullOrWhiteSpace(detail.Error)) Detail = detail;
                }
            }
            catch (Exception) { Message = "Không thêm được bản sao sách."; }
            finally { Busy = false; }
        }

        private async Task AddReferenceAsync(string kind, string name, string detail)
        {
            if (!CanManage) { Message = "Không có quyền quản lý danh mục."; return; }
            try
            {
                string result = await _references.AddAsync(_token, kind, name, detail);
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
