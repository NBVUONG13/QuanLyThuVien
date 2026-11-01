using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using QuanLyThuVien.ViewModels;

namespace QuanLyThuVien.Views
{
    public partial class BookInventoryView : UserControl
    {
        public BookInventoryView()
        {
            InitializeComponent();
        }

        // Retry each time the inventory page is shown. This never changes data.
        private async void InventoryView_Loaded(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as BookManagementViewModel;
            if (vm == null || !vm.CanManage || vm.InventoryBusy) return;
            await vm.RefreshInventoryAsync();
        }

        private void ChooseBatchShelf_Click(object sender, RoutedEventArgs e)
        {
            OpenShelfPicker(true);
        }

        private void ChooseCopyShelf_Click(object sender, RoutedEventArgs e)
        {
            OpenShelfPicker(false);
        }

        private void OpenShelfPicker(bool forBatch)
        {
            var vm = DataContext as BookManagementViewModel;
            if (vm == null) return;
            var current = forBatch ? vm.BatchShelf : vm.CopyShelf;
            var found = Regex.Match(current ?? "", @"DEMO-KE-0([1-6])(?:-T([1-4])-([A-D][1-4]))?", RegexOptions.IgnoreCase);
            string selectedShelf = found.Success ? "K" + found.Groups[1].Value : null;

            // Use the EXISTING library 2D map, not a second hand-drawn diagram.
            var mainVm = Application.Current.MainWindow == null ? null :
                Application.Current.MainWindow.DataContext as MainViewModel;
            var map = new LibraryMapView();
            map.DataContext = mainVm != null && mainVm.MapPage != null
                ? (object)mainVm.MapPage : new LibraryMapViewModel("");

            var selected = new TextBlock
            {
                Text = selectedShelf == null ? "Bấm trực tiếp vào một kệ K1–K6 trên sơ đồ" : "Đã chọn kệ " + selectedShelf,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = System.Windows.Media.Brushes.DarkSlateBlue,
                Margin = new Thickness(0, 0, 12, 0)
            };
            var floors = new ComboBox { Width = 115, Height = 32, Margin = new Thickness(8, 0, 16, 0) };
            foreach (var f in Enumerable.Range(1, 4)) floors.Items.Add(f);
            floors.SelectedItem = found.Success && found.Groups[2].Success
                ? int.Parse(found.Groups[2].Value) : 1;

            var slots = new ComboBox { Width = 115, Height = 32, Margin = new Thickness(8, 0, 14, 0) };
            foreach (char row in new[] { 'A', 'B', 'C', 'D' })
                foreach (int column in Enumerable.Range(1, 4))
                    slots.Items.Add(row.ToString() + column);
            slots.SelectedItem = found.Success && found.Groups[3].Success
                ? found.Groups[3].Value.ToUpperInvariant() : "A1";

            var accept = new Button
            {
                Content = "LƯU VỊ TRÍ ĐÃ CHỌN",
                Width = 185,
                Height = 35,
                Background = System.Windows.Media.Brushes.SlateBlue,
                Foreground = System.Windows.Media.Brushes.White,
                BorderThickness = new Thickness(0),
                IsEnabled = selectedShelf != null
            };
            var close = new Button { Content = "ĐÓNG", Width = 70, Height = 35, Margin = new Thickness(10, 0, 0, 0) };
            var panel = new WrapPanel { Margin = new Thickness(13, 12, 13, 10) };
            panel.Children.Add(selected);
            panel.Children.Add(new TextBlock { Text = "Tầng", VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(floors);
            panel.Children.Add(new TextBlock { Text = "Ô", VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(slots);
            panel.Children.Add(accept);
            panel.Children.Add(close);

            var root = new DockPanel();
            DockPanel.SetDock(panel, Dock.Top);
            root.Children.Add(panel);
            var canvasScroll = new ScrollViewer
            {
                Content = map,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            root.Children.Add(canvasScroll);
            var dialog = new Window
            {
                Title = "Chọn kệ · Tầng · Ô trên sơ đồ thư viện 2D",
                Owner = Application.Current.MainWindow,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Width = 1180,
                Height = 780,
                MinWidth = 920,
                MinHeight = 580,
                Content = root,
                Background = System.Windows.Media.Brushes.White
            };
            map.ShelfPicked += shelf =>
            {
                selectedShelf = shelf;
                selected.Text = "Đã chọn " + shelf;
                accept.IsEnabled = true;
            };
            accept.Click += (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(selectedShelf)) return;
                int shelfNumber = int.Parse(selectedShelf.Substring(1));
                string location = string.Format("DEMO-KE-{0:00}-T{1}-{2}", shelfNumber, floors.SelectedItem, slots.SelectedItem);
                if (forBatch) vm.BatchShelf = location;
                else vm.CopyShelf = location;
                dialog.DialogResult = true;
            };
            close.Click += (s, e) => dialog.Close();
            dialog.ShowDialog();
        }
    }
}
