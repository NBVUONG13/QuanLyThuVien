using System;
using System.Globalization;
using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;

namespace QuanLyThuVien.ViewModels
{
    // Step 82: Reader-only AI book assistant. This is a separate navigable page,
    // not a second book-detail or inventory ViewModel.
    public sealed class BookAssistantViewModel : MvvmBase
    {
        private readonly string _token;
        private readonly BookAssistantApiClient _api = new BookAssistantApiClient();
        private string _question = "";
        private string _status = "Hỏi về sách, hạn trả hoặc nghiệp vụ theo quyền tài khoản.";
        private bool _busy;
        private BookAssistantSuggestion _selectedBook;

        public BookAssistantViewModel(string token)
        {
            _token = token;
            Suggestions.CollectionChanged += (sender, args) => Notify(nameof(HasSuggestions));
            AskCommand = new AsyncCommand(_ => AskAsync(), _ => !Busy);
            OpenBookCommand = new ActionCommand(_ =>
            {
                if (SelectedBook != null) BookRequested?.Invoke(SelectedBook.BookId);
            });
            FindShelfCommand = new ActionCommand(_ =>
            {
                if (SelectedBook != null) FindShelfRequested?.Invoke(SelectedBook.BookId);
            });
            Messages.Add(new BookAssistantMessage
            {
                Speaker = "TRỢ LÝ THƯ VIỆN",
                Body = "Bạn có thể hỏi về sách, tình trạng kho và hạn trả của mình. Staff/Admin có thể hỏi thêm thống kê nghiệp vụ theo quyền."
            });
        }

        public event Action<long> BookRequested;
        public event Action<long> FindShelfRequested;
        public ObservableCollection<BookAssistantMessage> Messages { get; } = new ObservableCollection<BookAssistantMessage>();
        public ObservableCollection<BookAssistantSuggestion> Suggestions { get; } = new ObservableCollection<BookAssistantSuggestion>();
        public bool HasSuggestions { get { return Suggestions.Count > 0; } }
        public ICommand AskCommand { get; }
        public ICommand OpenBookCommand { get; }
        public ICommand FindShelfCommand { get; }

        public string Question { get { return _question; } set { Set(ref _question, value); } }
        public string Status { get { return _status; } private set { Set(ref _status, value); } }
        public bool Busy
        {
            get { return _busy; }
            private set { if (Set(ref _busy, value)) (AskCommand as AsyncCommand)?.Refresh(); }
        }
        public BookAssistantSuggestion SelectedBook
        {
            get { return _selectedBook; }
            set { Set(ref _selectedBook, value); }
        }

        // A live loan/due-date/fine question never receives an old or random catalog list.
        // Default is hidden: show cards only for explicit book discovery/recommendations.
        private static bool IsBookDiscoveryQuestion(string question)
        {
            string q = NormalizeQuestion(question);
            if (ContainsAny(q, "dang muon", "dang giu", "han tra", "het han",
                "qua han", "tien phat", "no phat", "lich su muon",
                "don muon", "gia han", "cham cong", "bang luong",
                "doc gia", "nhan vien", "thong bao", "tra sach")) return false;
            return ContainsAny(q,
                "goi y", "nen doc", "muon doc", "muon hoc", "sach nao hay",
                "tim sach", "co sach nao", "sach giong", "sach tuong tu",
                "gioi thieu sach", "gioi thieu the loai", "tu van sach",
                "the loai", "tac gia", "trinh tham", "lap trinh", "thuat toan",
                "truyen ve", "sach ve", "van hoc", "tieu thuyet", "kinh di");
        }

        private static bool ContainsAny(string q, params string[] patterns)
        {
            foreach (string pattern in patterns)
                if (q.Contains(pattern)) return true;
            return false;
        }

        private static string NormalizeQuestion(string question)
        {
            string raw = (question ?? "").ToLowerInvariant().Replace('đ', 'd')
                .Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(raw.Length);
            foreach (char c in raw)
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                    sb.Append(c);
            return sb.ToString().Normalize(NormalizationForm.FormC);
        }

        public async Task AskAsync()
        {
            if (Busy) return;
            string question = (Question ?? "").Trim();
            if (question.Length < 2 || question.Length > 400)
            {
                Status = "Câu hỏi cần từ 2 đến 400 ký tự.";
                return;
            }
            // Hide stale suggestions immediately, including while the request runs.
            Suggestions.Clear();
            SelectedBook = null;
            Messages.Add(new BookAssistantMessage { Speaker = "BẠN", Body = question });
            Question = "";
            Busy = true;
            Status = "Đang tra cứu dữ liệu thư viện...";
            try
            {
                var result = await _api.AskAsync(_token, question);
                if (!result.IsSuccess)
                {
                    Status = result.Message;
                    Messages.Add(new BookAssistantMessage { Speaker = "HỆ THỐNG", Body = result.Message });
                    return;
                }
                var response = result.Data;
                Messages.Add(new BookAssistantMessage
                {
                    Speaker = response.AiUsed ? "AI THƯ VIỆN (LM STUDIO)" : "TRA CỨU THƯ VIỆN",
                    Body = response.Reply ?? "Không có nội dung trả lời."
                });
                Suggestions.Clear();
                if (response.Suggestions != null && IsBookDiscoveryQuestion(question))
                    foreach (var book in response.Suggestions) Suggestions.Add(book);
                SelectedBook = Suggestions.Count > 0 ? Suggestions[0] : null;
                Status = response.Note ?? "Đã tra cứu xong.";
            }
            catch (Exception)
            {
                Status = "Không thể hoàn thành yêu cầu. Hãy kiểm tra kết nối rồi thử lại.";
                Messages.Add(new BookAssistantMessage { Speaker = "HỆ THỐNG", Body = Status });
            }
            finally { Busy = false; }
        }
    }

    public sealed class BookAssistantMessage
    {
        public string Speaker { get; set; }
        public string Body { get; set; }
    }

    [DataContract]
    public sealed class BookAssistantSuggestion
    {
        [DataMember(Name = "bookId")] public long BookId { get; set; }
        [DataMember(Name = "title")] public string Title { get; set; }
        [DataMember(Name = "author")] public string Author { get; set; }
        [DataMember(Name = "category")] public string Category { get; set; }
        [DataMember(Name = "publicationYear")] public int PublicationYear { get; set; }
        [DataMember(Name = "availableCopies")] public int AvailableCopies { get; set; }
        [DataMember(Name = "recentBorrows")] public int RecentBorrows { get; set; }
        public string AvailabilityText { get { return AvailableCopies > 0 ? AvailableCopies + " cuốn sẵn sàng" : "Hiện hết cuốn sẵn sàng"; } }
    }

    [DataContract]
    internal sealed class BookAssistantResponse
    {
        [DataMember(Name = "reply")] public string Reply { get; set; }
        [DataMember(Name = "aiUsed")] public bool AiUsed { get; set; }
        [DataMember(Name = "note")] public string Note { get; set; }
        [DataMember(Name = "suggestions")] public BookAssistantSuggestion[] Suggestions { get; set; }
    }

    [DataContract]
    internal sealed class BookAssistantRequest
    {
        [DataMember(Name = "question")] public string Question { get; set; }
    }

    internal sealed class BookAssistantResult
    {
        public bool IsSuccess { get; set; }
        public string Message { get; set; }
        public BookAssistantResponse Data { get; set; }
    }

    internal sealed class BookAssistantApiClient
    {
        private const string Url = "http://127.0.0.1:5082/api/reader/book-assistant/chat";
        private static readonly HttpClient Client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        public async Task<BookAssistantResult> AskAsync(string token, string question)
        {
            try
            {
                var json = new DataContractJsonSerializer(typeof(BookAssistantRequest));
                using (var stream = new MemoryStream())
                {
                    json.WriteObject(stream, new BookAssistantRequest { Question = question });
                    using (var request = new HttpRequestMessage(HttpMethod.Post, Url))
                    {
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                        request.Content = new StringContent(Encoding.UTF8.GetString(stream.ToArray()), Encoding.UTF8, "application/json");
                        using (var reply = await Client.SendAsync(request))
                        {
                            if (!reply.IsSuccessStatusCode)
                            {
                                string info = reply.StatusCode == HttpStatusCode.Unauthorized
                                    ? "Phiên đăng nhập hết hạn. Hãy đăng nhập lại."
                                    : reply.StatusCode == HttpStatusCode.Forbidden
                                    ? "Tài khoản không được phép sử dụng trợ lý."
                                    : reply.StatusCode == HttpStatusCode.NotFound
                                    ? "Backend chưa đăng ký API Chatbot (bước 82)."
                                    : "Không tải được gợi ý từ API (HTTP " + (int)reply.StatusCode + ").";
                                return new BookAssistantResult { Message = info };
                            }
                            byte[] bytes = await reply.Content.ReadAsByteArrayAsync();
                            using (var data = new MemoryStream(bytes))
                            {
                                var parsed = new DataContractJsonSerializer(typeof(BookAssistantResponse))
                                    .ReadObject(data) as BookAssistantResponse;
                                return parsed == null
                                    ? new BookAssistantResult { Message = "API trả về dữ liệu không hợp lệ." }
                                    : new BookAssistantResult { IsSuccess = true, Data = parsed };
                            }
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException ||
                ex is SerializationException || ex is IOException || ex is ArgumentException)
            {
                return new BookAssistantResult { Message = "Không kết nối hoặc đọc được dữ liệu từ API. Kiểm tra backend 127.0.0.1:5082." };
            }
        }
    }
}
