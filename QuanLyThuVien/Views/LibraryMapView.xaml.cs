using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace QuanLyThuVien.Views
{
    public partial class LibraryMapView : UserControl
    {
        // Optional picker event. Existing route/navigation screen keeps working unchanged.
        public event Action<string> ShelfPicked;
        private Border _highlightedShelf;

        public LibraryMapView()
        {
            InitializeComponent();
        }

        private void ShelfClick(object sender, MouseButtonEventArgs e)
        {
            if (ShelfPicked == null) return;
            var border = sender as Border;
            var shelf = border == null ? null : border.Tag as string;
            if (string.IsNullOrWhiteSpace(shelf)) return;
            if (_highlightedShelf != null)
            {
                _highlightedShelf.BorderBrush = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(138, 104, 69));
                _highlightedShelf.BorderThickness = new Thickness(3);
            }
            _highlightedShelf = border;
            border.BorderBrush = System.Windows.Media.Brushes.OrangeRed;
            border.BorderThickness = new Thickness(5);
            ShelfPicked(shelf);
            e.Handled = true;
        }
    }
}
