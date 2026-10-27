using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;

namespace QuanLyThuVien.ViewModels
{
    public abstract class MvvmBase : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (object.Equals(field, value)) return false;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            return true;
        }
        protected void Notify([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public sealed class ActionCommand : ICommand
    {
        private readonly Action<object> _action;
        private readonly Predicate<object> _allowed;
        public ActionCommand(Action<object> action, Predicate<object> allowed = null)
        {
            _action = action ?? throw new ArgumentNullException(nameof(action));
            _allowed = allowed;
        }
        public event EventHandler CanExecuteChanged;
        public bool CanExecute(object p) => _allowed == null || _allowed(p);
        public void Execute(object p) => _action(p);
        public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    public sealed class AsyncCommand : ICommand
    {
        private readonly Func<object, Task> _execute;
        private readonly Predicate<object> _allowed;
        private bool _executing;
        public AsyncCommand(Func<object, Task> execute, Predicate<object> allowed = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _allowed = allowed;
        }
        public event EventHandler CanExecuteChanged;
        public bool CanExecute(object p) => !_executing && (_allowed == null || _allowed(p));
        public async void Execute(object p)
        {
            if (!CanExecute(p)) return;
            _executing = true; Refresh();
            try { await _execute(p); }
            finally { _executing = false; Refresh(); }
        }
        public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
