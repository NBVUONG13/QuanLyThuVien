using System;
using System.Windows;
using QuanLyThuVien.ViewModels;

namespace QuanLyThuVien.Views
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;
        public MainWindow(string token)
        {
            InitializeComponent();
            _viewModel = new MainViewModel(token);
            DataContext = _viewModel;
            _viewModel.LogoutRequested += ViewModel_LogoutRequested;
            Loaded += MainWindow_Loaded;
            Closed += (s, e) => _viewModel.LogoutRequested -= ViewModel_LogoutRequested;
        }
        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= MainWindow_Loaded;
            await _viewModel.InitializeAsync();
        }
        private void ViewModel_LogoutRequested(object sender, EventArgs e)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => ViewModel_LogoutRequested(sender, e))); return; }
            var login = new LoginHostWindow();
            Application.Current.MainWindow = login;
            login.Show();
            Close();
        }
    }
}
