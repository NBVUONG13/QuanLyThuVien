using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
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
        // The real catalog is fetched by the existing API and cached for local suggestions.
        // Current API returns at most 100 books. No fake book rows are added.
        private readonly List<CatalogBook> _allBooks = new List<CatalogBook>();
        private CatalogBook _selectedSearchSuggestion;
        private bool _showSearchSuggestions, _suppressSearchSuggestions;

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
        // STEP 70: one review per Reader/book. No fake ratings are seeded.
        private BookRatingsResult _ratings;
        private string _ratingMessage = "", _ratingComment = "";
        private int? _selectedRatingScore;
        private bool _savingRating;
        public ObservableCollection<int> RatingScoreOptions { get; } =
            new ObservableCollection<int> { 1, 2, 3, 4, 5 };
        public ObservableCollection<BookRatingItem> RatingComments { get; } =
            new ObservableCollection<BookRatingItem>();
        public BookRatingsResult Ratings
        {
            get => _ratings;
            private set
            {
                if (!Set(ref _ratings, value)) return;
                Notify(nameof(RatingSummary));
                Notify(nameof(CanRateBook));
                (SaveRatingCommand as AsyncCommand)?.Refresh();
            }
        }
        public string RatingSummary => Ratings == null
            ? (string.IsNullOrWhiteSpace(RatingMessage) ? "Đang tải đánh giá..." : "Chưa tải được đánh giá")
            : Ratings.RatingCount == 0 ? "Chưa có đánh giá."
            : Ratings.AverageScore.ToString("0.0", CultureInfo.CurrentCulture) + "/5 sao · " +
              Ratings.RatingCount + " lượt đánh giá";
        public bool CanRateBook => IsReader && Ratings != null && Ratings.CanReview;
        public int? SelectedRatingScore
        {
            get => _selectedRatingScore;
            set { if (Set(ref _selectedRatingScore, value)) (SaveRatingCommand as AsyncCommand)?.Refresh(); }
        }
        public string RatingComment { get => _ratingComment; set => Set(ref _ratingComment, value ?? ""); }
        public string RatingMessage
        {
            get => _ratingMessage;
            private set { if (Set(ref _ratingMessage, value)) Notify(nameof(RatingSummary)); }
        }
        public ICommand SaveRatingCommand { get; }
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
            OpenRecommendationCommand = new AsyncCommand(async parameter =>
            {
                var recommendation = parameter as PersonalizedBookChoice;
                if (recommendation == null) return;
                try
                {
                    CatalogBook target = _allBooks.FirstOrDefault(b => b.BookId == recommendation.BookId);
                    if (target == null)
                    {
                        // Home may be filtered or its catalog API may cap the book list.
                        var result = await _catalog.SearchAsync(_token, recommendation.Title);
                        if (result.IsSuccess && result.Books != null)
                            target = result.Books.FirstOrDefault(b => b.BookId == recommendation.BookId);
                    }
                    if (target == null)
                    {
                        RecommendationMessage = "Sách chưa xuất hiện trong danh mục tải về. Hãy tìm theo tên sách.";
                        return;
                    }
                    SelectedBook = target;
                    await LoadDetailAsync(); // Existing detail + rating + loan-request flow.
                }
                catch (Exception)
                {
                    RecommendationMessage = "Không mở được sách gợi ý. Hãy thử tìm theo tên sách.";
                }
            });
            OpenBookCommand = new AsyncCommand(async book =>
            {
                if (!(book is CatalogBook selected)) return;
                SelectedBook = selected;
                await LoadDetailAsync();
            });
            // Navigate to the EXISTING Reader request cart; do not create a real loan here.
            BorrowBookCommand = new ActionCommand(_ =>
            {
                if (!IsReader || SelectedBook == null || Detail == null) return;
                var book = SelectedBook;
                DetailExpanded = false;
                ShowSearchSuggestions = false;
                BorrowBookRequested?.Invoke(book);
            }, _ => IsReader && SelectedBook != null && Detail != null && !Busy);
            RouteToBookCommand = new ActionCommand(_ =>
            {
                if (SelectedBook == null) return;
                var book = SelectedBook;
                DetailExpanded = false;
                RouteToBookRequested?.Invoke(book);
            }, _ => SelectedBook != null && Detail != null);
            SaveRatingCommand = new AsyncCommand(_ => SaveRatingAsync(),
                _ => CanRateBook && SelectedBook != null && SelectedRatingScore.HasValue && !_savingRating);
            DetailCommand = new AsyncCommand(_ => LoadDetailAsync(), _ => SelectedBook != null);
            CloseDetailCommand = new ActionCommand(_ => DetailExpanded = false);
            AddCopyCommand = new AsyncCommand(_ => AddCopyAsync(), _ => CanManage && SelectedBook != null);
            LoadReferencesCommand = new AsyncCommand(_ => LoadReferencesAsync(), _ => !_loadingReferences);
            AddAuthorCommand = new AsyncCommand(_ => AddReferenceAsync("authors", AuthorName, AuthorDetail), _ => CanManage);
            AddPublisherCommand = new AsyncCommand(_ => AddReferenceAsync("publishers", PublisherName, PublisherDetail), _ => CanManage);
            AddCategoryCommand = new AsyncCommand(_ => AddReferenceAsync("categories", CategoryName, ""), _ => CanManage);
            ReloadInventoryCommand = new AsyncCommand(_ => RefreshInventoryAsync(), _ => CanManage);
            NewInventoryBookCommand = new ActionCommand(_ => PrepareNextInventoryBook(), _ => CanManage);
            ResetInventoryFormCommand = new ActionCommand(_ => ResetInventoryBook(), _ => CanManage);
            SaveInventoryBookCommand = new AsyncCommand(_ => SaveInventoryBookAsync(), _ => CanManage);
            ReceiveStockCommand = new AsyncCommand(_ => ReceiveStockAsync(), _ => CanManage);
            SaveStockCopyCommand = new AsyncCommand(_ => SaveStockCopyAsync(), _ => CanManage);
            ReportLostCommand = new AsyncCommand(_ => ReportIncidentAsync("lost"), _ => CanManage);
            ReportDamagedCommand = new AsyncCommand(_ => ReportIncidentAsync("damaged"), _ => CanManage);
        }

        public bool CanManage { get; }
        public bool IsReader => !CanManage;
        // MainViewModel subscribes once to navigate to its existing BorrowRequestViewModel.
        public event Action<CatalogBook> BorrowBookRequested;
        public event Action<CatalogBook> RouteToBookRequested;
        public ICommand RouteToBookCommand { get; }
        public ObservableCollection<CatalogBook> SearchSuggestions { get; } = new ObservableCollection<CatalogBook>();
        public bool ShowSearchSuggestions
        {
            get => _showSearchSuggestions;
            set => Set(ref _showSearchSuggestions, value);
        }
        public bool NoSearchSuggestions => SearchSuggestions.Count == 0;
        public CatalogBook SelectedSearchSuggestion
        {
            get => _selectedSearchSuggestion;
            set
            {
                if (!Set(ref _selectedSearchSuggestion, value) || value == null) return;
                _suppressSearchSuggestions = true;
                Query = value.Title;
                _suppressSearchSuggestions = false;
                ShowSearchSuggestions = false;
                // Click suggestion => show this book's detailed information.
                _ = OpenSuggestionAsync(value);
            }
        }
        public ObservableCollection<CatalogBook> Books { get; } = new ObservableCollection<CatalogBook>();
        // STEP 83: personalized Reader book row on the existing Home, not a new page.
        public ObservableCollection<PersonalizedBookChoice> RecommendedBooks { get; } =
            new ObservableCollection<PersonalizedBookChoice>();
        private string _recommendationMessage = "Đang tải gợi ý...";
        public string RecommendationMessage
        {
            get => _recommendationMessage;
            private set => Set(ref _recommendationMessage, value);
        }
        public ICommand OpenRecommendationCommand { get; private set; }
        private static readonly HttpClient RecommendationHttp =
            new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

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
        public string Query
        {
            get => _query;
            set
            {
                if (Set(ref _query, value ?? "") && !_suppressSearchSuggestions)
                    RefreshSearchSuggestions();
            }
        }
        public string Message { get => _message; private set => Set(ref _message, value); }
        public bool Busy
        {
            get => _busy;
            private set
            {
                if (Set(ref _busy, value)) (BorrowBookCommand as ActionCommand)?.Refresh();
            }
        }
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
                Ratings = null;
                RatingComments.Clear();
                SelectedRatingScore = null;
                RatingComment = "";
                RatingMessage = "";
                Copies.Clear();
                (DetailCommand as AsyncCommand)?.Refresh();
                (AddCopyCommand as AsyncCommand)?.Refresh();
                (BorrowBookCommand as ActionCommand)?.Refresh();
                (RouteToBookCommand as ActionCommand)?.Refresh();
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
                (BorrowBookCommand as ActionCommand)?.Refresh();
                (RouteToBookCommand as ActionCommand)?.Refresh();
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


        public ICommand BorrowBookCommand { get; }
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
        public ICommand ReloadInventoryCommand { get; }
        public ICommand NewInventoryBookCommand { get; }
        public ICommand ResetInventoryFormCommand { get; }
        public ICommand SaveInventoryBookCommand { get; }
        public ICommand ReceiveStockCommand { get; }
        public ICommand SaveStockCopyCommand { get; }
        public ICommand ReportLostCommand { get; }
        public ICommand ReportDamagedCommand { get; }

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
            if (CanManage && !_inventoryLoaded) await RefreshInventoryAsync();
            if (IsReader) await LoadRecommendationsAsync();
        }

        private async Task LoadRecommendationsAsync()
        {
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get,
                    "http://127.0.0.1:5082/api/reader/recommendations"))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
                    using (var response = await RecommendationHttp.SendAsync(request))
                    {
                        if (response.StatusCode == HttpStatusCode.NotFound)
                        { RecommendationMessage = "API gợi ý chưa được đăng ký trên backend."; return; }
                        if (!response.IsSuccessStatusCode)
                        { RecommendationMessage = "Không tải được gợi ý (HTTP " + (int)response.StatusCode + ")."; return; }
                        byte[] body = await response.Content.ReadAsByteArrayAsync();
                        using (var ms = new MemoryStream(body))
                        {
                            var serializer = new DataContractJsonSerializer(typeof(PersonalizedBookResponse));
                            var result = serializer.ReadObject(ms) as PersonalizedBookResponse;
                            if (result == null)
                            { RecommendationMessage = "Dữ liệu gợi ý không hợp lệ."; return; }
                            RecommendedBooks.Clear();
                            if (result.Recommendations != null)
                                foreach (var item in result.Recommendations)
                                    RecommendedBooks.Add(item);
                            RecommendationMessage = result.Note ?? "Đã tải gợi ý.";
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException ||
                ex is SerializationException || ex is IOException || ex is InvalidDataContractException)
            {
                RecommendationMessage = "Không tải được gợi ý. Kiểm tra API và kết nối thư viện.";
            }
        }

        // Normalise Vietnamese accents, including Đ/đ, for fast local suggestions.
        private static string Fold(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var chars = value.ToLowerInvariant().Replace('đ', 'd').Normalize(NormalizationForm.FormD);
            var output = new StringBuilder(chars.Length);
            foreach (var c in chars)
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                    output.Append(c);
            return output.ToString().Normalize(NormalizationForm.FormC);
        }

        private static bool MatchesText(CatalogBook book, string normalized)
        {
            return Fold(book.Title).Contains(normalized) ||
                Fold(book.Authors).Contains(normalized) ||
                Fold(book.Isbn).Contains(normalized);
        }

        private void RefreshSearchSuggestions()
        {
            SearchSuggestions.Clear();
            var key = Fold((Query ?? "").Trim());
            if (key.Length == 0 || !_booksLoaded)
            {
                ShowSearchSuggestions = false;
                Notify(nameof(NoSearchSuggestions));
                return;
            }
            foreach (var book in _allBooks.Where(b => MatchesText(b, key)).Take(8))
                SearchSuggestions.Add(book);
            Notify(nameof(NoSearchSuggestions));
            ShowSearchSuggestions = true;
        }

        private async Task OpenSuggestionAsync(CatalogBook book)
        {
            if (book == null) return;
            try
            {
                await SearchAsync();
                SelectedBook = Books.FirstOrDefault(b => b.BookId == book.BookId) ?? book;
                await LoadDetailAsync();
            }
            catch (Exception) { Message = "Không thể mở sách được chọn."; }
        }

        public async Task SearchAsync()
        {
            if (Busy) return;
            ShowSearchSuggestions = false;
            Busy = true;
            Message = "Đang tìm sách...";
            try
            {
                // Refresh the real catalog on explicit Search/Reset/navigation.
                // Live dropdown on each keystroke only filters the cached data.
                // Existing API currently caps this result to 100 books.
                var result = await _catalog.SearchAsync(_token, "");
                if (!result.IsSuccess) { Message = result.Message; return; }
                _allBooks.Clear();
                if (result.Books != null) _allBooks.AddRange(result.Books);
                _booksLoaded = true;
                _searchResults.Clear();
                var key = Fold((Query ?? "").Trim());
                _searchResults.AddRange(string.IsNullOrEmpty(key)
                    ? _allBooks
                    : _allBooks.Where(b => MatchesText(b, key)));
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
                var preservedPublisherId = FormPublisher == null ? (long?)null : FormPublisher.Id;
                Replace(Publishers, result.Publishers);
                FormPublisher = Publishers.FirstOrDefault(p => p.Id == preservedPublisherId);
                Replace(Categories, result.Categories);
                _referencesLoaded = true;
                RebuildInventoryReferences();
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
                await LoadRatingsAsync(id);
            }
            catch (Exception) { DetailMessage = "Không tải được chi tiết sách."; }
            finally { Busy = false; }
        }

        private async Task LoadRatingsAsync(long id)
        {
            var response = await _detailApi.GetRatingsAsync(_token, id);
            if (SelectedBook == null || SelectedBook.BookId != id) return;
            if (!string.IsNullOrWhiteSpace(response.Error))
            {
                Ratings = null;
                RatingMessage = response.Error;
                return;
            }
            Ratings = response;
            Replace(RatingComments, response.Reviews);
            SelectedRatingScore = response.MyScore > 0 ? (int?)response.MyScore : null;
            RatingComment = response.MyComment ?? "";
            RatingMessage = IsReader && !response.CanReview
                ? "Bạn cần từng mượn đầu sách này mới được đánh giá."
                : "";
        }

        private async Task SaveRatingAsync()
        {
            if (!CanRateBook || !SelectedRatingScore.HasValue || SelectedBook == null || _savingRating) return;
            long id = SelectedBook.BookId;
            if ((RatingComment ?? "").Length > 1000)
            { RatingMessage = "Nhận xét không quá 1000 ký tự."; return; }
            _savingRating = true;
            (SaveRatingCommand as AsyncCommand)?.Refresh();
            try
            {
                string result = await _detailApi.SaveRatingAsync(
                    _token, id, SelectedRatingScore.Value, (RatingComment ?? "").Trim());
                if (SelectedBook == null || SelectedBook.BookId != id) return;
                if (result != "Đã lưu đánh giá sách.") { RatingMessage = result; return; }
                await LoadRatingsAsync(id);
                if (SelectedBook != null && SelectedBook.BookId == id)
                    RatingMessage = "Đã lưu đánh giá của bạn.";
            }
            catch (Exception) { if (SelectedBook?.BookId == id) RatingMessage = "Không lưu được đánh giá."; }
            finally
            {
                _savingRating = false;
                (SaveRatingCommand as AsyncCommand)?.Refresh();
            }
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
                    if (kind == "authors")
                    {
                        var added = InventoryAuthors.FirstOrDefault(a =>
                            string.Equals(a.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
                        if (added != null) added.Selected = true;
                        AuthorName = "";
                    }
                    if (kind == "publishers")
                    {
                        FormPublisher = Publishers.FirstOrDefault(p =>
                            string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
                        PublisherName = "";
                    }
                    if (kind == "categories")
                    {
                        var added = InventoryCategories.FirstOrDefault(c =>
                            string.Equals(c.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
                        if (added != null) added.Selected = true;
                        CategoryName = "";
                    }
                    Message = result;
                    InventoryMessage = result + " Đã chọn vào form nhập sách.";
                }
            }
            catch (Exception) { Message = "Không thêm được danh mục nền."; }
        }

        // STEP 77: browse Home and manage inventory on separate pages, sharing this VM.
        private bool _inventoryLoaded, _inventoryBusy, _formIsVisible = true, _confirmIncident;
        private long _formBookId;
        private string _inventoryMessage = "", _formTitle = "", _formIsbn = "", _formDescription = "",
            _formPublicationYear = "", _batchQuantity = "1", _batchShelf = "",
            _copyShelf = "", _copyStatus = "active", _copyNote = "",
            _compensation = "0", _incidentNote = "";
        private ReferenceEntry _formPublisher;
        private InventoryBookRow _selectedInventoryBook;
        private InventoryCopyRow _selectedStockCopy;

        public ObservableCollection<InventoryBookRow> InventoryBooks { get; } = new ObservableCollection<InventoryBookRow>();
        public ObservableCollection<InventoryCopyRow> StockCopies { get; } = new ObservableCollection<InventoryCopyRow>();
        public ObservableCollection<StockReferenceChoice> InventoryAuthors { get; } = new ObservableCollection<StockReferenceChoice>();
        public ObservableCollection<StockReferenceChoice> InventoryCategories { get; } = new ObservableCollection<StockReferenceChoice>();
        public string[] StockStatusOptions { get; } = { "active", "damaged", "lost", "retired" };
        public string InventoryMessage { get => _inventoryMessage; private set => Set(ref _inventoryMessage, value); }
        public bool InventoryBusy { get => _inventoryBusy; private set => Set(ref _inventoryBusy, value); }
        public long FormBookId { get => _formBookId; private set { if (Set(ref _formBookId, value)) Notify(nameof(FormBookHeader)); } }
        public string FormBookHeader => FormBookId == 0 ? "TẠO ĐẦU SÁCH MỚI" : "SỬA ĐẦU SÁCH #" + FormBookId;
        public string FormTitle { get => _formTitle; set => Set(ref _formTitle, value); }
        public string FormIsbn { get => _formIsbn; set => Set(ref _formIsbn, value); }
        public string FormDescription { get => _formDescription; set => Set(ref _formDescription, value); }
        public string FormPublicationYear { get => _formPublicationYear; set => Set(ref _formPublicationYear, value); }
        public bool FormIsVisible { get => _formIsVisible; set => Set(ref _formIsVisible, value); }
        public ReferenceEntry FormPublisher { get => _formPublisher; set => Set(ref _formPublisher, value); }
        public string BatchQuantity { get => _batchQuantity; set => Set(ref _batchQuantity, value); }
        public string BatchShelf { get => _batchShelf; set => Set(ref _batchShelf, value); }
        public string CopyShelf { get => _copyShelf; set => Set(ref _copyShelf, value); }
        public string CopyStatus { get => _copyStatus; set => Set(ref _copyStatus, value); }
        public string CopyNote { get => _copyNote; set => Set(ref _copyNote, value); }
        public string Compensation { get => _compensation; set => Set(ref _compensation, value); }
        public string IncidentNote { get => _incidentNote; set => Set(ref _incidentNote, value); }
        public bool ConfirmIncident { get => _confirmIncident; set => Set(ref _confirmIncident, value); }
        public InventoryBookRow SelectedInventoryBook
        {
            get => _selectedInventoryBook;
            set
            {
                if (!Set(ref _selectedInventoryBook, value)) return;
                if (value == null) return;
                _ = LoadInventoryBookAsync(value.BookId);
            }
        }
        public InventoryCopyRow SelectedStockCopy
        {
            get => _selectedStockCopy;
            set
            {
                if (!Set(ref _selectedStockCopy, value)) return;
                CopyShelf = value?.ShelfLocation ?? "";
                CopyStatus = value?.ConditionStatus ?? "active";
                CopyNote = value?.ConditionNote ?? "";
                Compensation = "0";
                IncidentNote = "";
                ConfirmIncident = false;
                Notify(nameof(SelectedStockLoanText));
            }
        }
        public string SelectedStockLoanText => SelectedStockCopy == null ? "Chưa chọn cuốn"
            : SelectedStockCopy.OnLoan ? "ĐANG MƯỢN — phiếu #" + SelectedStockCopy.OpenBorrowDetailId
                : "Đang ở kho — chỉ thay đổi tình trạng, không ghi phạt độc giả.";

        private void RebuildInventoryReferences()
        {
            var authorIds = new HashSet<long>(InventoryAuthors.Where(a => a.Selected).Select(a => a.Id));
            var categoryIds = new HashSet<long>(InventoryCategories.Where(a => a.Selected).Select(a => a.Id));
            Replace(InventoryAuthors, Authors.Select(a => new StockReferenceChoice(a.Id, a.Name, authorIds.Contains(a.Id))));
            Replace(InventoryCategories, Categories.Select(c => new StockReferenceChoice(c.Id, c.Name, categoryIds.Contains(c.Id))));
        }
        private void PrepareNextInventoryBook()
        {
            // Keep selected author/publisher/category; user may enter many books of one series.
            _selectedInventoryBook = null;
            Notify(nameof(SelectedInventoryBook));
            FormBookId = 0;
            FormTitle = "";
            FormIsbn = "";
            FormDescription = "";
            FormPublicationYear = "";
            FormIsVisible = true;
            StockCopies.Clear();
            SelectedStockCopy = null;
            InventoryMessage = "Sẵn sàng nhập đầu sách mới; tác giả, NXB, thể loại được giữ nguyên. Chỉ Reset toàn bộ mới xóa chúng.";
        }
        private void ResetInventoryBook()
        {
            PrepareNextInventoryBook();
            FormPublisher = null;
            foreach (var a in InventoryAuthors) a.Selected = false;
            foreach (var c in InventoryCategories) c.Selected = false;
            BatchQuantity = "1";
            BatchShelf = "";
            InventoryMessage = "Đã Reset toàn bộ form nhập sách.";
        }
        public async Task RefreshInventoryAsync()
        {
            if (!CanManage || InventoryBusy) return;
            InventoryBusy = true;
            InventoryMessage = "Đang tải danh sách kho từ API...";
            try
            {
                var result = await _detailApi.InventoryBooksAsync(_token);
                if (result == null)
                {
                    InventoryMessage = "API kho không trả kết quả. Kiểm tra kết nối 127.0.0.1:5082.";
                    return;
                }
                if (!string.IsNullOrWhiteSpace(result.Error))
                {
                    InventoryMessage = "Tải kho thất bại: " + result.Error;
                    return;
                }
                if (result.Books == null)
                {
                    InventoryMessage = "API trả về HTTP 200 nhưng thiếu danh sách 'books'. Kiểm tra BookInventoryEndpoints.";
                    return;
                }
                long selectedId = FormBookId;
                Replace(InventoryBooks, result.Books);
                _inventoryLoaded = true;
                // Preserve selected row and form without initiating another API load.
                _selectedInventoryBook = InventoryBooks.FirstOrDefault(x => x.BookId == selectedId);
                Notify(nameof(SelectedInventoryBook));
                InventoryMessage = InventoryBooks.Count == 0
                    ? "API đã tải nhưng database không có đầu sách nào trong danh sách kho."
                    : "Đã tải " + InventoryBooks.Count + " đầu sách từ kho thật. Chọn một hàng để sửa hoặc nhập cuốn.";
            }
            catch (Exception ex)
            {
                InventoryMessage = "Lỗi đọc kho trong WPF (" + ex.GetType().Name + "). Kiểm tra cửa sổ Output/API.";
            }
            finally { InventoryBusy = false; }
        }
        private async Task LoadInventoryBookAsync(long bookId)
        {
            if (!CanManage || InventoryBusy) return;
            InventoryBusy = true;
            try
            {
                var item = await _detailApi.InventoryBookAsync(_token, bookId);
                if (!string.IsNullOrWhiteSpace(item.Error) || item.Book == null)
                { InventoryMessage = item.Error ?? "Không tìm thấy sách."; return; }
                var b = item.Book;
                FormBookId = b.BookId;
                FormTitle = b.Title ?? "";
                FormIsbn = b.Isbn ?? "";
                FormDescription = b.Description ?? "";
                FormPublicationYear = b.PublicationYear.HasValue && b.PublicationYear.Value > 0
                    ? b.PublicationYear.Value.ToString() : "";
                FormPublisher = Publishers.FirstOrDefault(x => x.Id == b.PublisherId);
                FormIsVisible = b.IsVisible;
                var aIds = new HashSet<long>(b.AuthorIds ?? new List<long>());
                var cIds = new HashSet<long>(b.CategoryIds ?? new List<long>());
                foreach (var a in InventoryAuthors) a.Selected = aIds.Contains(a.Id);
                foreach (var c in InventoryCategories) c.Selected = cIds.Contains(c.Id);
                await LoadStockCopiesCoreAsync(bookId);
                InventoryMessage = "Đang sửa đầu sách #" + bookId + ". Có thể sửa tên, ISBN, tác giả, thể loại, NXB và kho.";
            }
            catch (Exception) { InventoryMessage = "Không tải được thông tin cần sửa."; }
            finally { InventoryBusy = false; }
        }
        private async Task LoadStockCopiesCoreAsync(long bookId)
        {
            var result = await _detailApi.InventoryCopiesAsync(_token, bookId);
            if (!string.IsNullOrWhiteSpace(result.Error)) { InventoryMessage = result.Error; return; }
            Replace(StockCopies, result.Copies);
            SelectedStockCopy = null;
        }
        private async Task SaveInventoryBookAsync()
        {
            if (!CanManage || InventoryBusy) return;
            if (string.IsNullOrWhiteSpace(FormTitle) || FormTitle.Trim().Length < 2)
            { InventoryMessage = "Nhập tên sách ít nhất 2 ký tự."; return; }
            int year;
            if (!string.IsNullOrWhiteSpace(FormPublicationYear) &&
                (!int.TryParse(FormPublicationYear.Trim(), out year) || year < 0 || year > 3000))
            { InventoryMessage = "Năm xuất bản phải là số từ 0 đến 3000."; return; }
            InventoryBusy = true;
            long existingId = FormBookId;
            try
            {
                var book = new InventoryBookEdit
                {
                    BookId = FormBookId,
                    Title = FormTitle.Trim(),
                    Isbn = (FormIsbn ?? "").Trim(),
                    Description = (FormDescription ?? "").Trim(),
                    PublicationYear = string.IsNullOrWhiteSpace(FormPublicationYear) ? (int?)null : int.Parse(FormPublicationYear.Trim()),
                    PublisherId = FormPublisher == null ? (long?)null : FormPublisher.Id,
                    AuthorIds = InventoryAuthors.Where(x => x.Selected).Select(x => x.Id).ToList(),
                    CategoryIds = InventoryCategories.Where(x => x.Selected).Select(x => x.Id).ToList(),
                    IsVisible = FormIsVisible
                };
                var result = await _detailApi.SaveInventoryBookAsync(_token, book);
                if (!string.IsNullOrWhiteSpace(result.Error)) { InventoryMessage = result.Error; return; }
                if (existingId == 0)
                {
                    // For rapid consecutive intake, preserve author/publisher/category choices.
                    PrepareNextInventoryBook();
                    InventoryMessage = "Đã tạo sách #" + result.BookId + ". Nhập tên và ISBN đầu sách tiếp theo; tác giả/NXB/thể loại giữ nguyên.";
                }
                else
                {
                    InventoryMessage = result.Message;
                }
            }
            catch (Exception) { InventoryMessage = "Không thể lưu thông tin sách."; }
            finally { InventoryBusy = false; }
            await RefreshInventoryAsync();
            await SearchAsync();
        }
        private async Task ReceiveStockAsync()
        {
            if (!CanManage || InventoryBusy || FormBookId <= 0)
            { InventoryMessage = "Chọn hoặc lưu đầu sách trước khi nhập cuốn."; return; }
            int quantity;
            if (!int.TryParse(BatchQuantity, out quantity) || quantity < 1 || quantity > 100)
            { InventoryMessage = "Nhập số cuốn từ 1 đến 100."; return; }
            InventoryBusy = true;
            long bookId = FormBookId;
            try
            {
                var result = await _detailApi.ReceiveStockAsync(_token, bookId, quantity, (BatchShelf ?? "").Trim());
                if (!string.IsNullOrWhiteSpace(result.Error)) { InventoryMessage = result.Error; return; }
                InventoryMessage = result.Message;
                await LoadStockCopiesCoreAsync(bookId);
            }
            catch (Exception) { InventoryMessage = "Không nhập thêm được kho."; }
            finally { InventoryBusy = false; }
            await RefreshInventoryAsync();
            await SearchAsync();
        }
        private async Task SaveStockCopyAsync()
        {
            if (!CanManage || InventoryBusy || SelectedStockCopy == null) return;
            if (SelectedStockCopy.OnLoan)
            { InventoryMessage = "Cuốn đang cho mượn; dùng báo mất/hỏng, không chỉnh trực tiếp."; return; }
            InventoryBusy = true;
            long bookId = FormBookId;
            try
            {
                var result = await _detailApi.EditInventoryCopyAsync(_token, new InventoryCopyEdit
                {
                    CopyId = SelectedStockCopy.CopyId,
                    ConditionStatus = CopyStatus,
                    ShelfLocation = CopyShelf,
                    ConditionNote = CopyNote
                });
                InventoryMessage = result.Error ?? result.Message;
                if (string.IsNullOrWhiteSpace(result.Error)) await LoadStockCopiesCoreAsync(bookId);
            }
            catch (Exception) { InventoryMessage = "Không sửa được bản sao."; }
            finally { InventoryBusy = false; }
            await RefreshInventoryAsync();
        }
        private async Task ReportIncidentAsync(string kind)
        {
            if (!CanManage || InventoryBusy || SelectedStockCopy == null ||
                !SelectedStockCopy.OnLoan || !SelectedStockCopy.OpenBorrowDetailId.HasValue)
            { InventoryMessage = "Chỉ báo mất/hỏng trên cuốn đang có phiếu mượn mở."; return; }
            if (!ConfirmIncident)
            { InventoryMessage = "Tích ô XÁC NHẬN trước khi đóng phiếu mượn và ghi bồi thường."; return; }
            decimal amount;
            string raw = (Compensation ?? "").Trim().Replace(" ", "");
            // Allow plain integers or culture-specific decimals, not thousands separators.
            if (!decimal.TryParse(raw, NumberStyles.Number, CultureInfo.CurrentCulture, out amount) &&
                !decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out amount))
            { InventoryMessage = "Tiền bồi thường phải là số hợp lệ (VND)."; return; }
            if (amount < 0 || amount > 100000000m || (IncidentNote ?? "").Trim().Length < 8)
            { InventoryMessage = "Tiền từ 0 đến 100 triệu VND và lý do phải ít nhất 8 ký tự."; return; }
            InventoryBusy = true;
            long bookId = FormBookId;
            try
            {
                var result = await _detailApi.ReportInventoryIncidentAsync(_token,
                    SelectedStockCopy.OpenBorrowDetailId.Value, kind, amount, IncidentNote.Trim());
                InventoryMessage = result.Error ?? result.Message;
                if (string.IsNullOrWhiteSpace(result.Error))
                {
                    ConfirmIncident = false;
                    await LoadStockCopiesCoreAsync(bookId);
                }
            }
            catch (Exception) { InventoryMessage = "Không ghi được sự cố sách."; }
            finally { InventoryBusy = false; }
            await RefreshInventoryAsync();
        }
    }

    [DataContract]
    public sealed class PersonalizedBookChoice
    {
        [DataMember(Name = "bookId")] public long BookId { get; set; }
        [DataMember(Name = "title")] public string Title { get; set; }
        [DataMember(Name = "authors")] public string Authors { get; set; }
        [DataMember(Name = "categories")] public string Categories { get; set; }
        [DataMember(Name = "availableCopies")] public int AvailableCopies { get; set; }
        [DataMember(Name = "coverUrl")] public string CoverUrl { get; set; }
        [DataMember(Name = "reason")] public string Reason { get; set; }
        public string Availability => AvailableCopies > 0
            ? "Còn " + AvailableCopies + " cuốn có thể mượn" : "Hiện chưa có cuốn sẵn sàng";
    }

    [DataContract]
    internal sealed class PersonalizedBookResponse
    {
        [DataMember(Name = "note")] public string Note { get; set; }
        [DataMember(Name = "personalized")] public bool Personalized { get; set; }
        [DataMember(Name = "recommendations")] public PersonalizedBookChoice[] Recommendations { get; set; }
    }

    // One checkbox item; no separate ViewModel or file for a reference selection.
    public sealed class StockReferenceChoice : MvvmBase
    {
        private bool _selected;
        public long Id { get; private set; }
        public string Name { get; private set; }
        public bool Selected { get => _selected; set => Set(ref _selected, value); }
        public StockReferenceChoice(long id, string name, bool selected)
        { Id = id; Name = name; _selected = selected; }
    }
}
