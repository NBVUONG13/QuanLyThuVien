using System;
using System.Threading.Tasks;
using System.Windows.Input;
using QuanLyThuVien.Services;

namespace QuanLyThuVien.ViewModels
{
    // Reader registration logic and OTP state; RegisterView.xaml only handles PasswordBox plumbing.
    public sealed class RegisterViewModel : MvvmBase
    {
        private readonly RegistrationApiClient _api = new RegistrationApiClient();
        private string _fullName = "", _username = "", _phone = "", _otp = "";
        private string _guestClaimCode = "";
        private string _message = "Nhập thông tin rồi chọn TẠO TÀI KHOẢN.";
        private Guid _requestId;
        private bool _busy, _completed, _guestProfileLoaded, _guestHasPhone;
        public RegisterViewModel()
        {
            StartCommand = new AsyncCommand(p => StartAsync(p as Tuple<string, string>), p => CanStart);
            ConfirmCommand = new AsyncCommand(_ => ConfirmAsync(), _ => CanConfirm);
            PreviewGuestCommand = new AsyncCommand(_ => PreviewGuestAsync(), _ => CanPreviewGuest);
        }
        public string FullName { get => _fullName; set => Set(ref _fullName, value); }
        public string Username { get => _username; set => Set(ref _username, value); }
        public string Phone { get => _phone; set => Set(ref _phone, value); }
        public string GuestClaimCode
        {
            get => _guestClaimCode;
            set { if (Set(ref _guestClaimCode, value)) InvalidateGuestPreview(); }
        }
        public bool GuestProfileLoaded { get => _guestProfileLoaded; private set { if (Set(ref _guestProfileLoaded, value)) RefreshState(); } }
        public bool CanEditPhone => CanEdit && !(GuestProfileLoaded && _guestHasPhone);
        public bool CanPreviewGuest => CanEdit &&
            !string.IsNullOrWhiteSpace(GuestClaimCode) && GuestClaimCode.Trim().Length == 8;
        public string Otp { get => _otp; set => Set(ref _otp, value); }
        public string Message { get => _message; private set => Set(ref _message, value); }
        public bool Busy { get => _busy; private set { if (Set(ref _busy, value)) RefreshState(); } }
        public bool Completed { get => _completed; private set { if (Set(ref _completed, value)) RefreshState(); } }
        public bool CanEdit => !Busy && !Completed && _requestId == Guid.Empty;
        public bool CanStart => CanEdit;
        public bool CanConfirm => !Busy && !Completed && _requestId != Guid.Empty;
        public ICommand StartCommand { get; }
        public ICommand ConfirmCommand { get; }
        public ICommand PreviewGuestCommand { get; }
        private void RefreshState()
        {
            Notify(nameof(CanEdit)); Notify(nameof(CanEditPhone)); Notify(nameof(CanStart));
            Notify(nameof(CanConfirm)); Notify(nameof(CanPreviewGuest));
            (StartCommand as AsyncCommand)?.Refresh();
            (ConfirmCommand as AsyncCommand)?.Refresh();
            (PreviewGuestCommand as AsyncCommand)?.Refresh();
        }
        private void InvalidateGuestPreview()
        {
            if (_guestProfileLoaded)
            {
                GuestProfileLoaded = false;
                _guestHasPhone = false;
                FullName = "";
                Phone = "";
            }
            RefreshState();
        }
        private async Task PreviewGuestAsync()
        {
            if (!CanPreviewGuest) return;
            Busy = true;
            Message = "Đang xác thực mã Staff và tải hồ sơ Guest...";
            try
            {
                var result = await _api.PreviewGuestAsync(GuestClaimCode);
                if (!result.IsSuccess) { Message = result.Message; return; }
                FullName = result.FullName;
                Phone = result.Phone;
                _guestHasPhone = !string.IsNullOrWhiteSpace(result.Phone);
                GuestProfileLoaded = true;
                Message = _guestHasPhone
                    ? "Đã tải hồ sơ cũ. Bạn có thể sửa HỌ TÊN; SĐT cũ giữ nguyên. Đổi SĐT cần Staff xác minh riêng."
                    : "Đã tải hồ sơ cũ. Hãy sửa HỌ TÊN đúng và nhập SĐT đang dùng.";
            }
            catch (Exception) { Message = "Không thể tải hồ sơ Guest từ API."; }
            finally { Busy = false; }
        }
        private async Task StartAsync(Tuple<string, string> passwords)
        {
            if (!CanStart) return;
            string password = passwords?.Item1;
            string confirmation = passwords?.Item2;
            if (string.IsNullOrWhiteSpace(FullName) || string.IsNullOrWhiteSpace(Username) ||
                string.IsNullOrWhiteSpace(Phone) || string.IsNullOrWhiteSpace(password))
            { Message = "Vui lòng nhập đầy đủ họ tên, tài khoản, SĐT và mật khẩu."; return; }
            if (!string.Equals(password, confirmation, StringComparison.Ordinal))
            { Message = "Hai lần nhập mật khẩu không trùng nhau."; return; }
            // Registration policy currently enforced in RegistrationService/API, not the admin 5-char policy.
            if (password.Length < 10 || password.Length > 128)
            { Message = "Mật khẩu đăng ký Reader cần 10–128 ký tự (theo API hiện tại)."; return; }
            if (!string.IsNullOrWhiteSpace(GuestClaimCode) && !GuestProfileLoaded)
            { Message = "Hãy bấm TẢI HỒ SƠ GUEST bằng mã 8 số do Staff cấp trước khi đăng ký."; return; }
            Busy = true; Message = "Đang gửi yêu cầu đăng ký...";
            try
            {
                var r = await _api.StartAsync(FullName.Trim(), Username.Trim(), Phone.Trim(), password,
                    GuestClaimCode);
                if (!r.IsSuccess) { Message = r.Message; return; }
                _requestId = r.RequestId;
                RefreshState();
                Message = "Đã tạo yêu cầu. Xem OTP ở cửa sổ API local (không gửi SMS).";
            }
            catch (Exception) { Message = "Không thể tạo yêu cầu đăng ký."; }
            finally { Busy = false; }
        }
        private async Task ConfirmAsync()
        {
            if (!CanConfirm) return;
            if (Otp == null || Otp.Length != 6 || !System.Text.RegularExpressions.Regex.IsMatch(Otp, @"\A[0-9]{6}\z"))
            { Message = "OTP gồm đúng 6 chữ số."; return; }
            Busy = true; Message = "Đang xác nhận OTP...";
            try
            {
                var r = await _api.ConfirmAsync(_requestId, Otp);
                if (!r.IsSuccess) { Message = r.Message; return; }
                Completed = true;
                _requestId = Guid.Empty;
                Otp = "";
                GuestClaimCode = "";
                Message = "Đăng ký Reader thành công: " + r.Username + ". Quay lại đăng nhập để sử dụng.";
            }
            catch (Exception) { Message = "Không thể xác nhận OTP."; }
            finally { Busy = false; }
        }
    }
}
