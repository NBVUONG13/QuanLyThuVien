using System;
using System.Windows;
using System.Windows.Controls;
using QuanLyThuVien.Services;
using QuanLyThuVien.ViewModels;

namespace QuanLyThuVien.Views
{
    public partial class LoginWindow : UserControl
    {
        private readonly LoginViewModel _vm;
        public LoginWindow()
        {
            InitializeComponent();
            _vm = new LoginViewModel();
            DataContext = _vm;
            _vm.LoginSucceeded += LoginSucceeded;
            btnLogin.Click += Login_Click;
            Unloaded += (s, e) => _vm.LoginSucceeded -= LoginSucceeded;
        }
        private void Login_Click(object sender, RoutedEventArgs e)
        {
            if (_vm.LoginCommand.CanExecute(pwdPassword.Password))
                _vm.LoginCommand.Execute(pwdPassword.Password);
        }
        private void LoginSucceeded(object sender, AuthLoginResult result)
        {
            if (string.IsNullOrWhiteSpace(result.Token)) return;
            pwdPassword.Clear();
            var main = new MainWindow(result.Token);
            Application.Current.MainWindow = main;
            main.Show();
            Window.GetWindow(this)?.Close();
        }
    }
}
