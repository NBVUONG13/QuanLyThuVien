using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using QuanLyThuVien.Services;

namespace QuanLyThuVien.Views
{
    // Dashboard local DEV. UI visibility is NOT server-side authorization.
    public sealed class HomeWindow : Window
    {
        private readonly AuthApiClient _authClient = new AuthApiClient();
        private readonly DashboardApiClient _dashboardClient = new DashboardApiClient();
        private readonly BookCatalogApiClient _bookClient = new BookCatalogApiClient();
        private readonly BookDetailApiClient _detailClient = new BookDetailApiClient();
        private readonly StackPanel _menu = new StackPanel();
        private readonly StackPanel _main = new StackPanel();
        private readonly TextBlock _notice = new TextBlock { TextWrapping = TextWrapping.Wrap };
        private readonly Button _logout = new Button { Content = "ĐĂNG XUẤT", Height = 40 };
        private string _token;
        private string _role;
        private string _username;
        private bool _signingOut;

        public HomeWindow(string token)
        {
            _token = token;
            Title = "SMART LIBRARY - Dashboard";
            Width = 1120; Height = 720; MinWidth = 740; MinHeight = 500;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Brush("#F3F6FB");

            var layout = new Grid();
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(245) });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var sidebar = new DockPanel { Background = Brush("#15263F"), LastChildFill = true };
            var bottom = new StackPanel { Margin = new Thickness(18, 8, 18, 20) };
            _logout.Click += Logout_Click;
            bottom.Children.Add(_logout);
            DockPanel.SetDock(bottom, Dock.Bottom);
            sidebar.Children.Add(bottom);
            var menuScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var menuWrap = new StackPanel { Margin = new Thickness(16, 24, 16, 8) };
            menuWrap.Children.Add(new TextBlock
            {
                Text = "SMART LIBRARY",
                Foreground = Brushes.White,
                FontSize = 21,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(7, 0, 0, 22)
            });
            menuWrap.Children.Add(_menu);
            menuScroll.Content = menuWrap;
            sidebar.Children.Add(menuScroll);
            Grid.SetColumn(sidebar, 0);
            layout.Children.Add(sidebar);

            var content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var contentBorder = new Border { Padding = new Thickness(30) };
            _main.Children.Add(new TextBlock { Text = "Đang xác minh phiên...", FontSize = 22 });
            contentBorder.Child = _main;
            content.Content = contentBorder;
            Grid.SetColumn(content, 1);
            layout.Children.Add(content);
            Content = layout;
            Loaded += HomeWindow_Loaded;
        }

        private static SolidColorBrush Brush(string hex)
        {
            return (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
        }

        private void AddMenu(string title, Action callback)
        {
            var button = new Button
            {
                Content = title,
                Height = 43,
                Margin = new Thickness(0, 3, 0, 3),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(13, 0, 5, 0),
                Foreground = Brushes.White,
                Background = Brush("#203957"),
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            button.Click += (s, e) => callback();
            _menu.Children.Add(button);
        }

        private void ConfigureMenu()
        {
            _menu.Children.Clear();
            AddMenu("Tổng quan", ShowDashboard);
            AddMenu("Danh mục sách", () => ShowBooks(""));
            AddMenu("Tìm kiếm sách", () => ShowBooks(""));
            if (_role == "Reader")
            {
                AddMenu("Sách đang mượn", () => ShowPending("Sách đang mượn"));
                AddMenu("Gia hạn", () => ShowPending("Gia hạn"));
                AddMenu("Tiền phạt", () => ShowPending("Tiền phạt"));
            }
            if (_role == "Staff" || _role == "Admin")
            {
                AddMenu("Quản lý độc giả", () => ShowPending("Quản lý độc giả"));
                AddMenu("Mượn / trả sách", () => ShowPending("Mượn / trả sách"));
                AddMenu("Quản lý bản sao", () => ShowBooks(""));
            }
            if (_role == "Admin")
            {
                AddMenu("Quản lý nhân viên", () => ShowPending("Quản lý nhân viên"));
                AddMenu("Chấm công / lương", () => ShowPending("Chấm công / lương"));
                AddMenu("Chính sách thư viện", () => ShowPending("Chính sách thư viện"));
                AddMenu("Sao lưu dữ liệu", () => ShowPending("Sao lưu dữ liệu"));
            }
        }

        private void Header(string heading)
        {
            _main.Children.Clear();
            _main.Children.Add(new TextBlock
            {
                Text = heading,
                FontSize = 28,
                FontWeight = FontWeights.Bold,
                Foreground = Brush("#172033"),
                Margin = new Thickness(0, 0, 0, 12)
            });
            _main.Children.Add(new TextBlock
            {
                Text = "Xin chào " + _username + "  •  Vai trò: " + _role,
                FontSize = 15,
                Foreground = Brush("#475569"),
                Margin = new Thickness(0, 0, 0, 24)
            });
        }


        private async void ShowBooks(string initialQuery)
        {
            Header("Danh mục & tìm kiếm sách");
            var searchRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 12)
            };
            var keyword = new TextBox
            {
                Width = 300,
                Height = 36,
                Text = initialQuery ?? "",
                Margin = new Thickness(0, 0, 10, 0),
                Padding = new Thickness(8, 6, 8, 4)
            };
            var find = new Button { Content = "Tìm kiếm", Width = 105, Height = 36 };
            searchRow.Children.Add(keyword);
            searchRow.Children.Add(find);
            _main.Children.Add(searchRow);
            var status = new TextBlock { Foreground = Brush("#475569"), Margin = new Thickness(0, 4, 0, 10) };
            _main.Children.Add(status);
            var table = new DataGrid
            {
                AutoGenerateColumns = false,
                IsReadOnly = true,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                Height = 430,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                Background = Brushes.White
            };
            table.Columns.Add(new DataGridTextColumn { Header = "Tên sách", Binding = new System.Windows.Data.Binding("Title"), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
            table.Columns.Add(new DataGridTextColumn { Header = "Tác giả", Binding = new System.Windows.Data.Binding("Authors"), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
            table.Columns.Add(new DataGridTextColumn { Header = "ISBN", Binding = new System.Windows.Data.Binding("Isbn"), Width = 140 });
            table.Columns.Add(new DataGridTextColumn { Header = "Nhà XB", Binding = new System.Windows.Data.Binding("Publisher"), Width = 160 });
            table.Columns.Add(new DataGridTextColumn { Header = "Năm", Binding = new System.Windows.Data.Binding("PublicationYear"), Width = 65 });
            table.Columns.Add(new DataGridTextColumn { Header = "Số cuốn", Binding = new System.Windows.Data.Binding("PhysicalCopies"), Width = 75 });
            _main.Children.Add(table);
            var editorContainer = new StackPanel();
            var detailButton = new Button
            {
                Content = "Xem chi tiết sách được chọn",
                Height = 38,
                Width = 240,
                Margin = new Thickness(0, 10, 0, 8),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            _main.Children.Add(detailButton);
            var detailText = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("#334155"),
                Margin = new Thickness(0, 8, 0, 12)
            };
            _main.Children.Add(detailText);
            _main.Children.Add(editorContainer);
            detailButton.Click += async (s, e) =>
            {
                editorContainer.Children.Clear();
                var selected = table.SelectedItem;
                if (selected == null) { detailText.Text = "Chọn một đầu sách trong bảng trước."; return; }
                var prop = selected.GetType().GetProperty("BookId");
                if (prop == null) { detailText.Text = "Không đọc được ID sách."; return; }
                var bookId = Convert.ToInt64(prop.GetValue(selected, null));
                var data = await _detailClient.GetDetailsAsync(_token, bookId);
                if (!string.IsNullOrEmpty(data.Error)) { detailText.Text = data.Error; return; }
                detailText.Text = data.Title + "\nTác giả: " + data.Authors +
                    "\nISBN: " + data.Isbn + " | NXB: " + data.Publisher +
                    "\nBản sao: " + data.TotalCopies + " | Tình trạng active: " + data.ActiveCopies +
                    "\nMô tả: " + data.Description;
                if (_role == "Staff" || _role == "Admin")
                    ShowCopyEditor(bookId, detailText, editorContainer);
            };
            async System.Threading.Tasks.Task LoadAsync()
            {
                find.IsEnabled = false;
                status.Text = "Đang tải danh mục từ PostgreSQL...";
                var result = await _bookClient.SearchAsync(_token, keyword.Text);
                if (result.IsSuccess)
                {
                    table.ItemsSource = result.Books;
                    status.Text = "Tìm thấy " + result.Books.Count + " đầu sách (tối đa 100 kết quả). Số cuốn là bản sao vật lý đã nhập.";
                }
                else status.Text = result.Message;
                find.IsEnabled = true;
            }
            find.Click += async (s, e) => await LoadAsync();
            keyword.KeyDown += async (s, e) => {
                if (e.Key == System.Windows.Input.Key.Enter) await LoadAsync();
            };
            await LoadAsync();
        }

        private void ShowCopyEditor(long bookId, TextBlock output, StackPanel container)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 12, 0, 12)
            };
            var code = new TextBox
            {
                Width = 170,
                Height = 34,
                Margin = new Thickness(0, 0, 8, 0),
                ToolTip = "Mã cuốn duy nhất (VD: LIB-0001)"
            };
            var shelf = new TextBox
            {
                Width = 145,
                Height = 34,
                Margin = new Thickness(0, 0, 8, 0),
                ToolTip = "Vị trí kệ sách"
            };
            var add = new Button { Content = "Thêm cuốn vật lý", Width = 145, Height = 34 };
            row.Children.Add(code); row.Children.Add(shelf); row.Children.Add(add);
            container.Children.Add(new TextBlock { Text = "Nhập mã cuốn và kệ (chỉ Staff/Admin):" });
            container.Children.Add(row);
            var copies = new DataGrid
            {
                AutoGenerateColumns = true,
                IsReadOnly = true,
                Height = 170,
                CanUserAddRows = false
            };
            container.Children.Add(copies);
            async System.Threading.Tasks.Task Refresh()
            {
                var list = await _detailClient.GetCopiesAsync(_token, bookId);
                if (!string.IsNullOrEmpty(list.Error)) output.Text = list.Error;
                else copies.ItemsSource = list.Copies;
            }
            add.Click += async (s, e) =>
            {
                add.IsEnabled = false;
                output.Text = await _detailClient.AddCopyAsync(_token, bookId, code.Text.Trim(), shelf.Text.Trim());
                await Refresh();
                add.IsEnabled = true;
            };
            var ignored = Refresh();
        }

        private void ShowPending(string heading)
        {
            Header(heading);
            _main.Children.Add(new TextBlock
            {
                Text =
                "Mục này đang trong lộ trình phát triển; chưa có thao tác nghiệp vụ hoặc API tương ứng.",
                FontSize = 16,
                Foreground = Brush("#64748B"),
                TextWrapping = TextWrapping.Wrap
            });
        }

        private async void ShowDashboard()
        {
            Header("Tổng quan thư viện");
            _notice.Text = "Đang tải thống kê từ PostgreSQL...";
            _main.Children.Add(_notice);
            var summary = await _dashboardClient.LoadAsync(_token);
            if (!summary.IsSuccess)
            {
                _notice.Text = summary.Message;
                return;
            }
            _notice.Text = "Dữ liệu thống kê được API đọc trực tiếp từ Aiven PostgreSQL.";
            var grid = new UniformGrid { Columns = 2, Margin = new Thickness(0, 20, 0, 0) };
            AddStat(grid, "Đầu sách", summary.Books);
            AddStat(grid, "Tác giả", summary.Authors);
            AddStat(grid, "Thể loại", summary.Categories);
            AddStat(grid, "Bản sao vật lý", summary.PhysicalCopies);
            AddStat(grid, "Hồ sơ độc giả", summary.Readers);
            _main.Children.Add(grid);
        }

        private static void AddStat(Panel panel, string caption, long value)
        {
            var card = new StackPanel { Background = Brushes.White, Margin = new Thickness(0, 0, 16, 16) };
            card.Children.Add(new TextBlock
            {
                Text = caption,
                FontSize = 14,
                Foreground = Brush("#64748B"),
                Margin = new Thickness(20, 18, 20, 5)
            });
            card.Children.Add(new TextBlock
            {
                Text = value.ToString("N0"),
                FontSize = 29,
                FontWeight = FontWeights.Bold,
                Foreground = Brush("#2563EB"),
                Margin = new Thickness(20, 0, 20, 19)
            });
            panel.Children.Add(card);
        }

        private async void HomeWindow_Loaded(object sender, RoutedEventArgs e)
        {
            _logout.IsEnabled = false;
            var session = await _authClient.ValidateAsync(_token);
            if (!session.IsSuccess ||
                (session.Role != "Reader" && session.Role != "Staff" && session.Role != "Admin"))
            {
                _main.Children.Clear();
                _main.Children.Add(new TextBlock { Text = "Phiên không hợp lệ. Hãy đăng nhập lại." });
                _logout.Content = "QUAY LẠI ĐĂNG NHẬP";
                _token = null;
                _logout.IsEnabled = true;
                return;
            }
            _role = session.Role;
            _username = session.Username;
            ConfigureMenu();
            _logout.IsEnabled = true;
            ShowDashboard();
        }

        private async void Logout_Click(object sender, RoutedEventArgs e)
        {
            if (_signingOut) return;
            _signingOut = true;
            _logout.IsEnabled = false;
            if (!string.IsNullOrWhiteSpace(_token))
                await _authClient.LogoutAsync(_token);
            _token = null;
            var login = new LoginHostWindow();
            login.Show();
            Close();
        }
    }
}
