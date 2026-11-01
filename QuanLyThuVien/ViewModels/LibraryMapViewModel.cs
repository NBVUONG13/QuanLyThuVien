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
    public sealed class LibraryMapViewModel : MvvmBase
    {
        private readonly string _token;
        private readonly LibraryMapApiClient _api = new LibraryMapApiClient();
        private readonly System.Collections.Generic.List<LibraryMapItem> _all =
            new System.Collections.Generic.List<LibraryMapItem>();
        private string _search = "", _message = "Chọn sách để xem đường tới kệ.",
                       _start = "Cửa vào", _destination = "Chưa chọn kệ", _shelfHint = "Chọn sách để xem kệ.";
        private LibraryMapItem _selected;
        private bool _busy, _hasRoute;
        private double _personX = 467, _personY = 607, _targetX, _targetY;
        private System.Windows.Media.PointCollection _routePoints = new System.Windows.Media.PointCollection();
        private static readonly int[] ShelfX = { 65, 210, 355, 500, 645, 790 };
        private const int Cell = 20;

        public LibraryMapViewModel(string token)
        {
            _token = token;
            ReloadCommand = new AsyncCommand(_ => LoadAsync(), _ => !Busy);
            ShowRouteCommand = new ActionCommand(_ => DrawRoute(), _ => SelectedBook != null && !Busy);
            StartOptions.Add("Cửa vào");
            StartOptions.Add("Quầy phục vụ");
            StartOptions.Add("Khu đọc sách");
        }

        public System.Collections.ObjectModel.ObservableCollection<LibraryMapItem> Matches { get; } =
            new System.Collections.ObjectModel.ObservableCollection<LibraryMapItem>();
        public System.Collections.ObjectModel.ObservableCollection<string> StartOptions { get; } =
            new System.Collections.ObjectModel.ObservableCollection<string>();
        public System.Collections.ObjectModel.ObservableCollection<LibraryMapArrow> Arrows { get; } =
            new System.Collections.ObjectModel.ObservableCollection<LibraryMapArrow>();
        public ICommand ReloadCommand { get; }
        public ICommand ShowRouteCommand { get; }
        public bool Busy
        {
            get => _busy;
            private set { if (Set(ref _busy, value)) { (ReloadCommand as AsyncCommand)?.Refresh(); (ShowRouteCommand as ActionCommand)?.Refresh(); } }
        }
        public string Search
        {
            get => _search;
            set { if (Set(ref _search, value ?? "")) Filter(); }
        }
        public string Message { get => _message; private set => Set(ref _message, value); }
        public string Destination { get => _destination; private set => Set(ref _destination, value); }
        public string ShelfHint { get => _shelfHint; private set => Set(ref _shelfHint, value); }
        public LibraryMapItem SelectedBook
        {
            get => _selected;
            set
            {
                if (!Set(ref _selected, value)) return;
                (ShowRouteCommand as ActionCommand)?.Refresh();
                DrawRoute();
            }
        }
        public string StartLocation
        {
            get => _start;
            set { if (Set(ref _start, value ?? "Cửa vào")) DrawRoute(); }
        }
        public bool HasRoute { get => _hasRoute; private set => Set(ref _hasRoute, value); }
        public double PersonX { get => _personX; private set => Set(ref _personX, value); }
        public double PersonY { get => _personY; private set => Set(ref _personY, value); }
        public double TargetX { get => _targetX; private set => Set(ref _targetX, value); }
        public double TargetY { get => _targetY; private set => Set(ref _targetY, value); }
        public System.Windows.Media.PointCollection RoutePoints
        {
            get => _routePoints;
            private set => Set(ref _routePoints, value);
        }

        public async Task LoadAsync(long preselectBookId = 0)
        {
            if (Busy) return;
            Busy = true;
            try
            {
                var response = await _api.GetAsync(_token);
                if (!string.IsNullOrEmpty(response.Error))
                {
                    Message = response.Error;
                    return;
                }
                _all.Clear();
                _all.AddRange(response.Items ?? new System.Collections.Generic.List<LibraryMapItem>());
                Filter();
                var selected = preselectBookId > 0
                    ? _all.Where(v => v.BookId == preselectBookId)
                          .OrderByDescending(v => v.AvailableCopies).FirstOrDefault()
                    : _selected == null ? null : _all.FirstOrDefault(v =>
                        v.BookId == _selected.BookId && v.ShelfLocation == _selected.ShelfLocation);
                if (selected != null)
                {
                    if (!Matches.Contains(selected)) { Search = selected.Title; }
                    SelectedBook = selected;
                }
                else if (preselectBookId > 0)
                {
                    SelectedBook = null;
                    Message = "Đầu sách này chưa có vị trí kệ trong book_copies.";
                }
                else if (SelectedBook == null && Matches.Count > 0)
                    SelectedBook = Matches[0];
                else if (_all.Count == 0)
                    Message = "Không có cuốn nào được gán vị trí kệ. Hãy kiểm tra book_copies.shelf_location.";
            }
            finally { Busy = false; }
        }

        private void Filter()
        {
            var term = (Search ?? "").Trim();
            var current = SelectedBook;
            Matches.Clear();
            foreach (var item in _all.Where(x => term.Length == 0
                || (x.Title ?? "").IndexOf(term, StringComparison.CurrentCultureIgnoreCase) >= 0
                || (x.ShelfLocation ?? "").IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                .Take(120)) Matches.Add(item);
            // Do not reset the selected destination while the visitor types a search.
        }

        private static int ShelfNumber(string shelfLocation)
        {
            string s = (shelfLocation ?? "").Trim().ToUpperInvariant();
            var m = System.Text.RegularExpressions.Regex.Match(s,
                @"^(?:DEMO-KE-0?|KE-0?|K)([1-6])(?=$|[^0-9])");
            return m.Success ? int.Parse(m.Groups[1].Value) : 0;
        }

        private void DrawRoute()
        {
            Arrows.Clear();
            RoutePoints = new System.Windows.Media.PointCollection();
            HasRoute = false;
            var book = SelectedBook;
            if (book == null)
            {
                Destination = "Chưa chọn kệ";
                Message = "Chọn đầu sách để xem sơ đồ đường đi.";
                return;
            }
            int shelf = ShelfNumber(book.ShelfLocation);
            Destination = (shelf > 0 ? "Kệ K" + shelf : "Kệ " + book.ShelfLocation)
                        + " · Còn " + book.AvailableCopies + " cuốn có thể mượn";
            if (shelf == 0)
            {
                Message = "Vị trí '" + book.ShelfLocation + "' chưa được cấu hình trên sơ đồ DEMO 6 kệ. "
                    + "Không tự vẽ đường tới vị trí không biết.";
                ShelfHint = "Kệ này chưa có tọa độ trên bản đồ.";
                return;
            }
            int destinationX = ShelfX[shelf - 1] + 110;
            int destinationY = 350;
            TargetX = destinationX - 17;
            TargetY = destinationY - 17;
            var start = StartLocation == "Quầy phục vụ"
                ? new System.Windows.Point(280, 520)
                : StartLocation == "Khu đọc sách"
                    ? new System.Windows.Point(480, 220)
                    : new System.Windows.Point(480, 620);
            var path = FindPath(start, new System.Windows.Point(destinationX, destinationY));
            if (path.Count == 0)
            {
                Message = "Không tìm được lối đi hợp lệ trong sơ đồ DEMO.";
                return;
            }
            var corners = Simplify(path);
            var points = new System.Windows.Media.PointCollection();
            foreach (var p in corners) points.Add(p);
            RoutePoints = points;
            HasRoute = true;
            BuildArrows(corners);
            PersonX = start.X - 13;
            PersonY = start.Y - 13;
            var location = System.Text.RegularExpressions.Regex.Match(book.ShelfLocation ?? "",
                @"(?i)-T([1-4])-([A-D][1-4])$");
            ShelfHint = location.Success
                ? "KỆ K" + shelf + "  •  TẦNG " + location.Groups[1].Value + "  •  Ô " + location.Groups[2].Value.ToUpperInvariant()
                : "KỆ K" + shelf + "  •  Chưa gán tầng / ô";
            Message = "Mã cuốn gợi ý: " +
                      (string.IsNullOrWhiteSpace(book.SampleCopyCode) ? "chưa có cuốn khả dụng" : book.SampleCopyCode);
        }

        private void BuildArrows(System.Collections.Generic.List<System.Windows.Point> corners)
        {
            for (int i = 1; i < corners.Count; i++)
            {
                double dx = corners[i].X - corners[i - 1].X;
                double dy = corners[i].Y - corners[i - 1].Y;
                double length = Math.Sqrt(dx * dx + dy * dy);
                if (length < 28) continue;
                double angle = Math.Atan2(dy, dx) * 180.0 / Math.PI;
                for (double t = 48; t <= length - 10; t += 85)
                {
                    Arrows.Add(new LibraryMapArrow
                    {
                        X = corners[i - 1].X + dx * t / length - 10,
                        Y = corners[i - 1].Y + dy * t / length - 10,
                        Angle = angle
                    });
                }
            }
        }

        // Grid BFS avoids the six shelves and the reception desk. 0.5m/cell is a demo estimate.
        private static System.Collections.Generic.List<System.Windows.Point> FindPath(
            System.Windows.Point start, System.Windows.Point end)
        {
            const int maxX = 49, maxY = 33;
            int[,] prev = new int[maxX, maxY];
            for (int x = 0; x < maxX; x++) for (int y = 0; y < maxY; y++) prev[x, y] = -2;
            int sx = (int)Math.Round(start.X / Cell), sy = (int)Math.Round(start.Y / Cell);
            int tx = (int)Math.Round(end.X / Cell), ty = (int)Math.Round(end.Y / Cell);
            if (!Walkable(sx, sy) || !Walkable(tx, ty)) return new System.Collections.Generic.List<System.Windows.Point>();
            var q = new System.Collections.Generic.Queue<int>();
            prev[sx, sy] = -1;
            q.Enqueue(sy * maxX + sx);
            int[] dx = { 0, 1, 0, -1 }, dy = { -1, 0, 1, 0 };
            while (q.Count > 0 && prev[tx, ty] == -2)
            {
                int v = q.Dequeue();
                int x = v % maxX, y = v / maxX;
                for (int i = 0; i < 4; i++)
                {
                    int nx = x + dx[i], ny = y + dy[i];
                    if (nx < 0 || nx >= maxX || ny < 0 || ny >= maxY
                        || prev[nx, ny] != -2 || !Walkable(nx, ny)) continue;
                    prev[nx, ny] = v;
                    q.Enqueue(ny * maxX + nx);
                }
            }
            var result = new System.Collections.Generic.List<System.Windows.Point>();
            if (prev[tx, ty] == -2) return result;
            for (int x = tx, y = ty; ;)
            {
                result.Add(new System.Windows.Point(x * Cell, y * Cell));
                int p = prev[x, y];
                if (p == -1) break;
                x = p % maxX;
                y = p / maxX;
            }
            result.Reverse();
            return result;
        }
        private static bool Walkable(int gridX, int gridY)
        {
            int x = gridX * Cell, y = gridY * Cell;
            if (x < 20 || x > 960 || y < 20 || y > 640) return false;
            for (int i = 0; i < ShelfX.Length; i++)
                if (x >= ShelfX[i] - 9 && x <= ShelfX[i] + 85 && y >= 254 && y <= 478)
                    return false;
            if (x >= 40 && x <= 253 && y >= 530 && y <= 610) return false; // desk
            if (x >= 745 && x <= 930 && y >= 530 && y <= 620) return false; // new-books display
            return true;
        }
        private static System.Collections.Generic.List<System.Windows.Point> Simplify(
            System.Collections.Generic.List<System.Windows.Point> points)
        {
            if (points.Count <= 2) return new System.Collections.Generic.List<System.Windows.Point>(points);
            var result = new System.Collections.Generic.List<System.Windows.Point> { points[0] };
            for (int i = 1; i < points.Count - 1; i++)
            {
                double ax = points[i].X - points[i - 1].X;
                double ay = points[i].Y - points[i - 1].Y;
                double bx = points[i + 1].X - points[i].X;
                double by = points[i + 1].Y - points[i].Y;
                if (ax != bx || ay != by) result.Add(points[i]);
            }
            result.Add(points[points.Count - 1]);
            return result;
        }
    }

    public sealed class LibraryMapArrow
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Angle { get; set; }
    }

}
