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
            AddMenu("Danh mục sách", () => ShowPending("Danh mục sách"));
            AddMenu("Tìm kiếm sách", () => ShowPending("Tìm kiếm sách"));
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
                AddMenu("Quản lý bản sao", () => ShowPending("Quản lý bản sao"));
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
