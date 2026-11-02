using System;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using QuanLyThuVien.ViewModels;

namespace QuanLyThuVien.Views
{
    public partial class BookAssistantView : UserControl
    {
        private BookAssistantViewModel _attached;

        public BookAssistantView()
        {
            InitializeComponent();
            DataContextChanged += OnChatDataContextChanged;
            Loaded += (sender, e) => Attach(DataContext as BookAssistantViewModel);
            Unloaded += OnChatUnloaded;
            Attach(DataContext as BookAssistantViewModel);
        }

        // Enter in the input invokes the SAME AskCommand as the GỬI button.
        // Mark the key handled so it cannot submit twice.
        private void QuestionTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Return && e.Key != Key.Enter) return;
            e.Handled = true;
            var vm = DataContext as BookAssistantViewModel;
            if (vm != null && vm.AskCommand.CanExecute(null))
                vm.AskCommand.Execute(null);
        }

        private void OnChatDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            Attach(e.NewValue as BookAssistantViewModel);
        }

        private void OnChatUnloaded(object sender, RoutedEventArgs e)
        {
            // Detach when the Window closes; Attach is repeated on a later load if reused.
            Attach(null);
        }

        private void Attach(BookAssistantViewModel next)
        {
            if (ReferenceEquals(_attached, next)) return;
            if (_attached != null)
                _attached.Messages.CollectionChanged -= OnMessagesChanged;
            _attached = next;
            if (_attached != null)
            {
                _attached.Messages.CollectionChanged += OnMessagesChanged;
                ScrollToLatest();
            }
        }

        private void OnMessagesChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            ScrollToLatest();
        }

        private void ScrollToLatest()
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (ConversationScrollViewer != null)
                    ConversationScrollViewer.ScrollToEnd();
            }));
        }
    }
}
