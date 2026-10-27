using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using QuanLyThuVien.Services;
using QuanLyThuVien.ViewModels;

namespace QuanLyThuVien.Views
{
    public partial class LoginWindow : UserControl
    {
        private readonly LoginViewModel _viewModel;

        public LoginWindow()
        {
            InitializeComponent();

            _viewModel = new LoginViewModel();
            DataContext = _viewModel;

            // Giu nguyen LoginWindow.xaml cu: txtUsername, pwdPassword, btnLogin, txtMessage.
            txtUsername.TextChanged += Username_TextChanged;
            btnLogin.Click += Login_Click;
            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
            _viewModel.LoginSucceeded += ViewModel_LoginSucceeded;

            UpdateUi();
        }

        private void Username_TextChanged(object sender, TextChangedEventArgs e)
        {
            _viewModel.Username = txtUsername.Text;
        }

        private void Login_Click(object sender, RoutedEventArgs e)
        {
            // Chi chuyen mat khau vao command; ViewModel khong luu mat khau.
            if (_viewModel.LoginCommand.CanExecute(pwdPassword.Password))
            {
                _viewModel.LoginCommand.Execute(pwdPassword.Password);
            }
            else if (string.IsNullOrWhiteSpace(txtUsername.Text))
            {
                txtMessage.Text = "Vui lòng nhập tên đăng nhập.";
            }
        }

        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(LoginViewModel.Message) ||
                e.PropertyName == nameof(LoginViewModel.IsBusy) ||
                e.PropertyName == nameof(LoginViewModel.Username))
            {
                UpdateUi();
            }
        }

        private void UpdateUi()
        {
            txtMessage.Text = _viewModel.Message;
            btnLogin.IsEnabled = !_viewModel.IsBusy;
            btnLogin.Content = _viewModel.IsBusy ? "ĐANG ĐĂNG NHẬP..." : "ĐĂNG NHẬP";
        }

        private void ViewModel_LoginSucceeded(object sender, AuthLoginResult user)
        {
            pwdPassword.Clear();
            if (string.IsNullOrWhiteSpace(user.Token))
            {
                txtMessage.Text = "API chua cap phien dang nhap.";
                return;
            }
            // Mo trang chu SAU khi nhan token tu API; quyen phai duoc API xac nhan.
            var home = new HomeWindow(user.Token);
            home.Show();
            Window host = Window.GetWindow(this);
            if (host != null) host.Close();
        }
    }
}
