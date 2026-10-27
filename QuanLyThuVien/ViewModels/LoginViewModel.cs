using System;
using System.Threading.Tasks;
using System.Windows.Input;
using QuanLyThuVien.Services;

namespace QuanLyThuVien.ViewModels
{
    public sealed class LoginViewModel : MvvmBase
    {
        private readonly AuthApiClient _api;
        private string _username = "";
        private string _message = "";
        private bool _isBusy;
        public event EventHandler<AuthLoginResult> LoginSucceeded;
        public LoginViewModel() : this(new AuthApiClient()) { }
        public LoginViewModel(AuthApiClient api)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            LoginCommand = new AsyncCommand(p => LoginAsync(p as string), p => !IsBusy && !string.IsNullOrWhiteSpace(Username));
        }
        public string Username
        {
            get => _username;
            set
            {
                if (!Set(ref _username, value)) return;
                (LoginCommand as AsyncCommand)?.Refresh();
            }
        }
        public string Message { get => _message; private set => Set(ref _message, value); }
        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (!Set(ref _isBusy, value)) return;
                (LoginCommand as AsyncCommand)?.Refresh();
            }
        }
        public ICommand LoginCommand { get; }
        private async Task LoginAsync(string password)
        {
            if (IsBusy) return;
            if (string.IsNullOrEmpty(password)) { Message = "Vui lòng nhập mật khẩu."; return; }
            IsBusy = true;
            Message = "Đang xác thực...";
            try
            {
                var result = await _api.LoginAsync(Username, password);
                if (!result.IsSuccess) { Message = result.Message; return; }
                Message = "Đăng nhập hợp lệ: " + result.Username;
                LoginSucceeded?.Invoke(this, result);
            }
            catch (Exception)
            {
                Message = "Không thể đăng nhập. Vui lòng thử lại.";
            }
            finally { IsBusy = false; }
        }
    }
}
