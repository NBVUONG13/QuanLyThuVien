using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
            txtUsername.TextChanged += Username_TextChanged;
            btnLogin.Click += Login_Click;
            // Enter when focus is in password: same login handler, no separate authentication.
            pwdPassword.KeyDown += Password_KeyDown;
            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
            _viewModel.LoginSucceeded += ViewModel_LoginSucceeded;
            UpdateUi();
        }

        private void Username_TextChanged(object sender, TextChangedEventArgs e)
        {
            _viewModel.Username = txtUsername.Text;
        }

        private void Password_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Return && e.Key != Key.Enter) return;
            e.Handled = true;
            if (!_viewModel.IsBusy)
                Login_Click(btnLogin, new RoutedEventArgs(Button.ClickEvent, btnLogin));
        }

        private void Login_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel.IsBusy) return;
            // Password is passed only as the command parameter, not saved on the view model.
            if (_viewModel.LoginCommand.CanExecute(pwdPassword.Password))
                _viewModel.LoginCommand.Execute(pwdPassword.Password);
            else if (string.IsNullOrWhiteSpace(txtUsername.Text))
                txtMessage.Text = "Vui lòng nhập tên đăng nhập.";
        }

        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(LoginViewModel.Message) ||
                e.PropertyName == nameof(LoginViewModel.IsBusy) ||
                e.PropertyName == nameof(LoginViewModel.Username))
                UpdateUi();
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
                txtMessage.Text = "API chưa cấp phiên đăng nhập.";
                return;
            }
            var home = new MainWindow(user.Token);
            home.Show();
            Window host = Window.GetWindow(this);
            if (host != null) host.Close();
        }
    }
}
