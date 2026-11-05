using System;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using QuanLyThuVien.ViewModels;

namespace QuanLyThuVien.Views
{
    /// <summary>
    /// Registration form and a centered OTP verification overlay.
    /// Business rules and API calls remain in RegisterViewModel/RegistrationApiClient.
    /// </summary>
    public partial class RegisterView : UserControl
    {
        private RegisterViewModel _vm = new RegisterViewModel();
        private bool _otpOverlayOpened;

        public RegisterView()
        {
            InitializeComponent();
            DataContext = _vm;

            btnRegister.Click += Start_Click;
            btnConfirmOtp.Click += Confirm_Click;
            _vm.PropertyChanged += ViewModel_PropertyChanged;
        }

        private void Start_Click(object sender, RoutedEventArgs e)
        {
            var passwords = Tuple.Create(pwdRegisterPassword.Password,
                                         pwdConfirmPassword.Password);
            if (_vm.StartCommand.CanExecute(passwords))
                _vm.StartCommand.Execute(passwords);
        }

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            if (_vm.ConfirmCommand.CanExecute(null))
                _vm.ConfirmCommand.Execute(null);
        }

        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // RegisterViewModel raises CanConfirm after the send-OTP API has returned a requestId.
            // Do NOT show the OTP dialog while mail sending is still in progress or has failed.
            if (e.PropertyName == nameof(RegisterViewModel.CanConfirm) &&
                _vm.CanConfirm && !_vm.Completed && !_otpOverlayOpened)
            {
                OpenOtpOverlay();
            }
            else if (e.PropertyName == nameof(RegisterViewModel.Completed) && _vm.Completed)
            {
                ShowRegistrationSuccess();
            }
        }

        private void OpenOtpOverlay()
        {
            _otpOverlayOpened = true;
            OtpEntryPanel.Visibility = Visibility.Visible;
            OtpSuccessPanel.Visibility = Visibility.Collapsed;
            OtpOverlay.Visibility = Visibility.Visible;

            // Wipe password boxes now that the backend has accepted the request.
            // The submitted password is not needed again to confirm the OTP.
            pwdRegisterPassword.Clear();
            pwdConfirmPassword.Clear();

            Dispatcher.BeginInvoke(new Action(() =>
            {
                txtRegisterOtp.Focus();
                Keyboard.Focus(txtRegisterOtp);
            }), DispatcherPriority.Input);
        }

        private void ShowRegistrationSuccess()
        {
            _otpOverlayOpened = true;
            OtpOverlay.Visibility = Visibility.Visible;
            OtpEntryPanel.Visibility = Visibility.Collapsed;
            OtpSuccessPanel.Visibility = Visibility.Visible;
            txtRegisterOtp.Clear();
        }

        private void BackToRegister_Click(object sender, RoutedEventArgs e)
        {
            if (!_vm.ResetCommand.CanExecute(null)) return;

            _vm.ResetCommand.Execute(null);
            txtRegisterOtp.Clear();
            OtpOverlay.Visibility = Visibility.Collapsed;
            _otpOverlayOpened = false;
            // User must re-enter passwords to start a new request.
            txtRegisterEmail.Focus();
        }

        private void BackToLogin_Click(object sender, RoutedEventArgs e)
        {
            // Ready the registration screen for another person after a successful signup.
            if (_vm.Completed)
            {
                _vm.PropertyChanged -= ViewModel_PropertyChanged;
                _vm = new RegisterViewModel();
                DataContext = _vm;
                _vm.PropertyChanged += ViewModel_PropertyChanged;
                pwdRegisterPassword.Clear();
                pwdConfirmPassword.Clear();
                _otpOverlayOpened = false;
                OtpOverlay.Visibility = Visibility.Collapsed;
                OtpEntryPanel.Visibility = Visibility.Visible;
                OtpSuccessPanel.Visibility = Visibility.Collapsed;
            }

            Window host = Window.GetWindow(this);
            var toggle = host == null
                ? null
                : host.FindName("toggleRegister") as ToggleButton;

            if (toggle != null)
            {
                toggle.IsChecked = false;
            }
            else
            {
                // If hosted in a different shell, close the successful registration dialog.
                OtpOverlay.Visibility = Visibility.Collapsed;
                _otpOverlayOpened = false;
            }
        }

        private void Otp_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = !Regex.IsMatch(e.Text, @"\A[0-9]+\z");
        }

        private void Otp_Pasting(object sender, DataObjectPastingEventArgs e)
        {
            if (!e.DataObject.GetDataPresent(DataFormats.Text))
            {
                e.CancelCommand();
                return;
            }
            var pasted = e.DataObject.GetData(DataFormats.Text) as string;
            if (string.IsNullOrEmpty(pasted) ||
                !Regex.IsMatch(pasted, @"\A[0-9]{1,6}\z"))
            {
                e.CancelCommand();
            }
        }

        private void Otp_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter && e.Key != Key.Return) return;
            e.Handled = true;
            Confirm_Click(btnConfirmOtp, new RoutedEventArgs(Button.ClickEvent, btnConfirmOtp));
        }

        private void RegisterView_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape || !_otpOverlayOpened || _vm.Completed) return;
            if (!_vm.ResetCommand.CanExecute(null)) return;
            e.Handled = true;
            BackToRegister_Click(btnBackToRegister,
                new RoutedEventArgs(Button.ClickEvent, btnBackToRegister));
        }
    }
}
