using System;
using System.Windows;
using System.Windows.Controls;
using QuanLyThuVien.Services;

namespace QuanLyThuVien.Views
{
    public partial class RegisterView : UserControl
    {
        private readonly RegistrationApiClient _api = new RegistrationApiClient();
        private Guid _requestId = Guid.Empty;
        private bool _isBusy;
        private bool _isCompleted;

        public RegisterView()
        {
            InitializeComponent();

            // RegisterView.xaml step 37 has these named controls.
            btnRegister.Click += BtnRegister_Click;
            btnConfirmOtp.Click += BtnConfirmOtp_Click;

            // The XAML initially sets IsEnabled=False; code-behind unlocks
            // the appropriate button when the view is initialized.
            UpdateControls();
            txtRegisterMessage.Text = "Nhập thông tin rồi chọn TẠO TÀI KHOẢN.";
        }

        private async void BtnRegister_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy || _isCompleted || _requestId != Guid.Empty)
                return;

            string fullName = txtFullName.Text.Trim();
            string username = txtRegisterUsername.Text.Trim();
            string phone = txtRegisterPhone.Text.Trim();
            string password = pwdRegisterPassword.Password;
            string confirmation = pwdConfirmPassword.Password;

            if (string.IsNullOrWhiteSpace(fullName) ||
                string.IsNullOrWhiteSpace(username) ||
                string.IsNullOrWhiteSpace(phone) ||
                string.IsNullOrWhiteSpace(password))
            {
                txtRegisterMessage.Text = "Vui lòng nhập đầy đủ họ tên, tài khoản, SĐT và mật khẩu.";
                return;
            }

            if (!string.Equals(password, confirmation, StringComparison.Ordinal))
            {
                txtRegisterMessage.Text = "Hai lần nhập mật khẩu không trùng nhau.";
                return;
            }

            if (password.Length < 10 || password.Length > 128)
            {
                txtRegisterMessage.Text = "Mật khẩu phải dài từ 10 đến 128 ký tự, có chữ và số.";
                return;
            }

            _isBusy = true;
            UpdateControls();
            txtRegisterMessage.Text = "Đang gửi yêu cầu đăng ký...";

            try
            {
                RegistrationStartResult result = await _api.StartAsync(
                    fullName, username, phone, password);

                if (!result.IsSuccess)
                {
                    txtRegisterMessage.Text = result.Message;
                    return;
                }

                _requestId = result.RequestId;
                // After step 1, do not allow changing the user information.
                txtFullName.IsEnabled = false;
                txtRegisterUsername.IsEnabled = false;
                txtRegisterPhone.IsEnabled = false;
                pwdRegisterPassword.IsEnabled = false;
                pwdConfirmPassword.IsEnabled = false;
                txtRegisterMessage.Text =
                    "Đã tạo yêu cầu. Xem mã OTP trong cửa sổ chạy SmartLibrary.Api " +
                    "(chỉ bản thử nghiệm local; mã hết hạn sau 5 phút).";
            }
            catch (Exception)
            {
                txtRegisterMessage.Text = "Không thể hoàn thành yêu cầu. Vui lòng thử lại.";
            }
            finally
            {
                _isBusy = false;
                UpdateControls();
            }
        }

        private async void BtnConfirmOtp_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy || _isCompleted || _requestId == Guid.Empty)
                return;

            string otp = txtRegisterOtp.Text.Trim();
            if (otp.Length != 6 || !IsAllDigits(otp))
            {
                txtRegisterMessage.Text = "Mã OTP phải gồm đúng 6 chữ số.";
                return;
            }

            _isBusy = true;
            UpdateControls();
            txtRegisterMessage.Text = "Đang xác nhận mã OTP...";

            try
            {
                RegistrationConfirmResult result =
                    await _api.ConfirmAsync(_requestId, otp);

                if (!result.IsSuccess)
                {
                    txtRegisterMessage.Text = result.Message +
                        " Nếu mã đã hết hạn, chuyển về Đăng nhập rồi mở lại Đăng ký để tạo yêu cầu mới.";
                    return;
                }

                _isCompleted = true;
                _requestId = Guid.Empty;
                pwdRegisterPassword.Clear();
                pwdConfirmPassword.Clear();
                txtRegisterOtp.Clear();
                txtRegisterMessage.Text =
                    "Đăng ký Reader thành công: " + result.Username +
                    ". Chọn 'Quay lại đăng nhập' để đăng nhập.";
            }
            catch (Exception)
            {
                txtRegisterMessage.Text = "Không thể xác nhận mã. Vui lòng thử lại.";
            }
            finally
            {
                _isBusy = false;
                UpdateControls();
            }
        }

        private void UpdateControls()
        {
            btnRegister.IsEnabled = !_isBusy && !_isCompleted && _requestId == Guid.Empty;
            btnConfirmOtp.IsEnabled = !_isBusy && !_isCompleted && _requestId != Guid.Empty;
            txtRegisterOtp.IsEnabled = !_isCompleted && _requestId != Guid.Empty;

            btnRegister.Content = _isBusy && _requestId == Guid.Empty
                ? "ĐANG XỬ LÝ..." : "TẠO TÀI KHOẢN";
            btnConfirmOtp.Content = _isBusy && _requestId != Guid.Empty
                ? "ĐANG XÁC NHẬN..." : "XÁC NHẬN MÃ OTP";
        }

        private static bool IsAllDigits(string text)
        {
            foreach (char ch in text)
            {
                if (ch < '0' || ch > '9') return false;
            }
            return true;
        }
    }
}
