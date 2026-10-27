using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using QuanLyThuVien.Services;

namespace QuanLyThuVien.ViewModels
{
    // ViewModel khong biet toi cac control WPF. Buoc tiep theo se noi View vao command.
    public sealed class LoginViewModel : INotifyPropertyChanged
    {
        private readonly AuthApiClient _authApiClient;
        private string _username = string.Empty;
        private string _message = string.Empty;
        private bool _isBusy;
        private AuthLoginResult _authenticatedUser;

        public LoginViewModel() : this(new AuthApiClient()) { }

        public LoginViewModel(AuthApiClient authApiClient)
        {
            _authApiClient = authApiClient ?? throw new ArgumentNullException(nameof(authApiClient));
            LoginCommand = new LoginRelayCommand(ExecuteLoginAsync, CanLogin);
        }

        public event PropertyChangedEventHandler PropertyChanged;

        // Chi la thong tin danh tinh sau khi API xac minh mat khau.
        // API chua phat token/session nen khong duoc dung thong tin nay lam bang chung phan quyen API.
        public event EventHandler<AuthLoginResult> LoginSucceeded;

        public string Username
        {
            get { return _username; }
            set
            {
                if (_username == value) return;
                _username = value ?? string.Empty;
                OnPropertyChanged();
                ((LoginRelayCommand)LoginCommand).RaiseCanExecuteChanged();
            }
        }

        public string Message
        {
            get { return _message; }
            private set
            {
                if (_message == value) return;
                _message = value;
                OnPropertyChanged();
            }
        }

        public bool IsBusy
        {
            get { return _isBusy; }
            private set
            {
                if (_isBusy == value) return;
                _isBusy = value;
                OnPropertyChanged();
                ((LoginRelayCommand)LoginCommand).RaiseCanExecuteChanged();
            }
        }

        public AuthLoginResult AuthenticatedUser
        {
            get { return _authenticatedUser; }
            private set
            {
                if (ReferenceEquals(_authenticatedUser, value)) return;
                _authenticatedUser = value;
                OnPropertyChanged();
            }
        }

        // CommandParameter nhan chuoi mat khau tu PasswordBox o lop View.
        // Khong luu mat khau vao property, log, hay database cua WPF.
        public ICommand LoginCommand { get; }

        private bool CanLogin(object parameter)
        {
            return !IsBusy && !string.IsNullOrWhiteSpace(Username);
        }

        private async Task ExecuteLoginAsync(object parameter)
        {
            if (IsBusy) return;

            string password = parameter as string;
            if (string.IsNullOrEmpty(password))
            {
                Message = "Vui lòng nhập mật khẩu.";
                return;
            }

            IsBusy = true;
            Message = "Đang kiểm tra tài khoản...";
            AuthenticatedUser = null;

            try
            {
                var result = await _authApiClient.LoginAsync(Username, password);
                if (!result.IsSuccess)
                {
                    Message = result.Message;
                    return;
                }

                AuthenticatedUser = result;
                Message = "Đăng nhập hợp lệ: " + result.Username + " (" + result.Role + ")";
                LoginSucceeded?.Invoke(this, result);
            }
            catch (Exception)
            {
                // Khong hien exception he thong hay du lieu nhay cam tren UI.
                Message = "Có lỗi khi xử lý đăng nhập. Vui lòng thử lại.";
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private sealed class LoginRelayCommand : ICommand
        {
            private readonly Func<object, Task> _execute;
            private readonly Predicate<object> _canExecute;

            public LoginRelayCommand(Func<object, Task> execute, Predicate<object> canExecute)
            {
                _execute = execute;
                _canExecute = canExecute;
            }

            public event EventHandler CanExecuteChanged;

            public bool CanExecute(object parameter)
            {
                return _canExecute == null || _canExecute(parameter);
            }

            public async void Execute(object parameter)
            {
                // WPF ICommand.Execute la void; ngoai le da duoc bat trong ExecuteLoginAsync.
                await _execute(parameter);
            }

            public void RaiseCanExecuteChanged()
            {
                CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}
