using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
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
    // Step 81: one admin page, no changes to existing Dashboard or HR modules.
    public sealed class LibraryReportsViewModel : MvvmBase
    {
        private readonly string _token;
        private readonly LibraryReportsApiClient _api = new LibraryReportsApiClient();
        private DateTime? _fromDate, _toDate;
        private bool _busy;
        private string _message = "Chọn khoảng ngày, sau đó bấm XEM BÁO CÁO.";
        private LibraryReportSummary _summary = new LibraryReportSummary();
        private string _note = "";

        public LibraryReportsViewModel(string token)
        {
            _token = token;
            DateTime today = DateTime.UtcNow.AddHours(7).Date;
            _fromDate = new DateTime(today.Year, today.Month, 1);
            _toDate = today;
            ReloadCommand = new AsyncCommand(_ => LoadAsync(), _ => !Busy);
        }
        public DateTime? FromDate { get { return _fromDate; } set { Set(ref _fromDate, value); } }
        public DateTime? ToDate { get { return _toDate; } set { Set(ref _toDate, value); } }
        public bool Busy
        {
            get { return _busy; }
            private set
            {
                if (Set(ref _busy, value)) (ReloadCommand as AsyncCommand)?.Refresh();
            }
        }
        public string Message { get { return _message; } private set { Set(ref _message, value); } }
        public string Note { get { return _note; } private set { Set(ref _note, value); } }
        public LibraryReportSummary Summary { get { return _summary; } private set { Set(ref _summary, value); } }
        public ICommand ReloadCommand { get; private set; }
        public ObservableCollection<LibraryPopularBook> TopBooks { get; private set; } = new ObservableCollection<LibraryPopularBook>();
        public ObservableCollection<LibraryDailyRow> Daily { get; private set; } = new ObservableCollection<LibraryDailyRow>();
        public ObservableCollection<LibraryOverdueRow> Overdue { get; private set; } = new ObservableCollection<LibraryOverdueRow>();
        public ObservableCollection<LibraryFineRow> FineBreakdown { get; private set; } = new ObservableCollection<LibraryFineRow>();

        public async Task LoadAsync()
        {
            if (Busy) return;
            if (!FromDate.HasValue || !ToDate.HasValue || FromDate.Value.Date > ToDate.Value.Date ||
                (ToDate.Value.Date - FromDate.Value.Date).TotalDays > 365)
            {
                Message = "Khoảng ngày không hợp lệ. Chọn tối đa 366 ngày và ngày bắt đầu không lớn hơn ngày kết thúc.";
                return;
            }
            Busy = true;
            Message = "Đang tải báo cáo từ database...";
            try
            {
                var result = await _api.GetAsync(_token, FromDate.Value.Date, ToDate.Value.Date);
                if (!result.IsSuccess)
                {
                    Message = result.Message;
                    return; // Preserve the previous successfully loaded report on failure.
                }
                var report = result.Report;
                Summary = report.Summary ?? new LibraryReportSummary();
                Replace(TopBooks, report.TopBooks);
                Replace(Daily, report.Daily);
                Replace(Overdue, report.Overdue);
                Replace(FineBreakdown, report.FineBreakdown);
                Note = report.Note ?? "";
                Message = "Đã tải báo cáo từ " + report.From + " đến " + report.To + ".";
            }
            finally { Busy = false; }
        }
        private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> source)
        {
            target.Clear();
            if (source != null) foreach (var item in source) target.Add(item);
        }
    }

    // HTTP is encapsulated in its own class; kept in the same module file to avoid creating
    // a separate file just for a single read-only page.
    internal sealed class LibraryReportsApiClient
    {
        private const string Url = "http://127.0.0.1:5082/api/admin/library-reports";
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        public async Task<LibraryReportLoadResult> GetAsync(string token, DateTime from, DateTime to)
        {
            string url = Url + "?from=" + from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                + "&to=" + to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    using (var response = await Http.SendAsync(request))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            string error = response.StatusCode == HttpStatusCode.Unauthorized ? "Phiên đăng nhập hết hạn."
                                : response.StatusCode == HttpStatusCode.Forbidden ? "Chỉ Admin được phép xem báo cáo."
                                : "Không tải được báo cáo, HTTP " + (int)response.StatusCode + ".";
                            return new LibraryReportLoadResult { Message = error };
                        }
                        byte[] bytes = await response.Content.ReadAsByteArrayAsync();
                        using (var stream = new MemoryStream(bytes))
                        {
                            var serializer = new DataContractJsonSerializer(typeof(LibraryReportData));
                            var data = serializer.ReadObject(stream) as LibraryReportData;
                            if (data == null) return new LibraryReportLoadResult { Message = "API trả về báo cáo trống." };
                            return new LibraryReportLoadResult { IsSuccess = true, Report = data };
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException ||
                ex is SerializationException || ex is IOException || ex is ArgumentException)
            { return new LibraryReportLoadResult { Message = "Không kết nối hoặc đọc được dữ liệu báo cáo từ API." }; }
        }
    }
    internal sealed class LibraryReportLoadResult
    {
        public bool IsSuccess { get; set; }
        public string Message { get; set; }
        public LibraryReportData Report { get; set; }
    }
    [DataContract]
    internal sealed class LibraryReportData
    {
        [DataMember(Name = "from")] public string From { get; set; }
        [DataMember(Name = "to")] public string To { get; set; }
        [DataMember(Name = "note")] public string Note { get; set; }
        [DataMember(Name = "summary")] public LibraryReportSummary Summary { get; set; }
        [DataMember(Name = "topBooks")] public List<LibraryPopularBook> TopBooks { get; set; }
        [DataMember(Name = "daily")] public List<LibraryDailyRow> Daily { get; set; }
        [DataMember(Name = "overdue")] public List<LibraryOverdueRow> Overdue { get; set; }
        [DataMember(Name = "fineBreakdown")] public List<LibraryFineRow> FineBreakdown { get; set; }
    }
    [DataContract]
    public sealed class LibraryReportSummary
    {
        [DataMember(Name = "borrowOrders")] public long BorrowOrders { get; set; }
        [DataMember(Name = "borrowedCopies")] public long BorrowedCopies { get; set; }
        [DataMember(Name = "returnedCopies")] public long ReturnedCopies { get; set; }
        [DataMember(Name = "overdueNow")] public long OverdueNow { get; set; }
        [DataMember(Name = "visibleBooks")] public long VisibleBooks { get; set; }
        [DataMember(Name = "availableCopies")] public long AvailableCopies { get; set; }
        [DataMember(Name = "assessedFines")] public decimal AssessedFines { get; set; }
        [DataMember(Name = "collectedFines")] public decimal CollectedFines { get; set; }
        [DataMember(Name = "outstandingFines")] public decimal OutstandingFines { get; set; }
    }
    [DataContract]
    public sealed class LibraryPopularBook
    {
        [DataMember(Name = "bookId")] public long BookId { get; set; }
        [DataMember(Name = "title")] public string Title { get; set; }
        [DataMember(Name = "copies")] public long Copies { get; set; }
        [DataMember(Name = "readers")] public long Readers { get; set; }
    }
    [DataContract]
    public sealed class LibraryDailyRow
    {
        [DataMember(Name = "day")] public string Day { get; set; }
        [DataMember(Name = "orders")] public long Orders { get; set; }
        [DataMember(Name = "copies")] public long Copies { get; set; }
    }
    [DataContract]
    public sealed class LibraryOverdueRow
    {
        [DataMember(Name = "readerCode")] public string ReaderCode { get; set; }
        [DataMember(Name = "title")] public string Title { get; set; }
        [DataMember(Name = "copyCode")] public string CopyCode { get; set; }
        [DataMember(Name = "dueAt")] public string DueAt { get; set; }
        [DataMember(Name = "daysLate")] public int DaysLate { get; set; }
    }
    [DataContract]
    public sealed class LibraryFineRow
    {
        [DataMember(Name = "fineType")] public string FineType { get; set; }
        [DataMember(Name = "count")] public long Count { get; set; }
        [DataMember(Name = "assessed")] public decimal Assessed { get; set; }
        [DataMember(Name = "collected")] public decimal Collected { get; set; }
        public string FineTypeLabel
        {
            get { return FineType == "overdue" ? "Quá hạn" : FineType == "lost" ? "Mất sách" : FineType == "damaged" ? "Hỏng sách" : FineType; }
        }
    }
}
