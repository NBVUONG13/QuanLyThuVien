using System;
using System.Windows;
using System.Windows.Controls;
using QuanLyThuVien.ViewModels;

namespace QuanLyThuVien.Views
{
    public partial class RegisterView : UserControl
    {
        private readonly RegisterViewModel _vm = new RegisterViewModel();
        public RegisterView()
        {
            InitializeComponent();
            DataContext = _vm;
            btnRegister.Click += Start_Click;
            btnConfirmOtp.Click += Confirm_Click;
        }
        private void Start_Click(object sender, RoutedEventArgs e)
        {
            var credentials = Tuple.Create(pwdRegisterPassword.Password, pwdConfirmPassword.Password);
            if (_vm.StartCommand.CanExecute(credentials))
                _vm.StartCommand.Execute(credentials);
        }
        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            if (_vm.ConfirmCommand.CanExecute(null))
                _vm.ConfirmCommand.Execute(null);
        }
    }
}
