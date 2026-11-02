using System;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
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

        // Enter performs exactly the same command as the GUI Send button.
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

    /// <summary>
    /// Minimal safe Markdown display for the LM Studio answer. Render text via WPF Runs
    /// (never XAML/HTML injection). No third-party package or project-file changes.
    /// Handles paragraphs, bullet lists, headings, bold, italics and inline code.
    /// </summary>
    public static class ChatMarkdown
    {
        public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
            "Text", typeof(string), typeof(ChatMarkdown),
            new PropertyMetadata(string.Empty, OnTextChanged));

        public static string GetText(DependencyObject obj)
        {
            return (string)obj.GetValue(TextProperty);
        }

        public static void SetText(DependencyObject obj, string value)
        {
            obj.SetValue(TextProperty, value);
        }

        private static void OnTextChanged(DependencyObject obj, DependencyPropertyChangedEventArgs args)
        {
            var block = obj as TextBlock;
            if (block == null) return;
            block.Inlines.Clear();
            string content = (args.NewValue as string ?? string.Empty)
                .Replace("\r\n", "\n").Replace('\r', '\n');
            if (content.Length == 0) return;

            bool inCodeBlock = false;
            string[] lines = content.Split('\n');
            for (int n = 0; n < lines.Length; n++)
            {
                if (n > 0) block.Inlines.Add(new LineBreak());
                string text = lines[n].TrimEnd();
                string trimmed = text.TrimStart();

                if (trimmed.StartsWith("```", StringComparison.Ordinal))
                {
                    inCodeBlock = !inCodeBlock;
                    continue;
                }
                if (inCodeBlock)
                {
                    block.Inlines.Add(new Run(text)
                    {
                        FontFamily = new FontFamily("Consolas"),
                        Foreground = new SolidColorBrush(Color.FromRgb(63, 57, 74))
                    });
                    continue;
                }

                // Preserve natural indentation and render both Markdown and Unicode bullets.
                int indent = text.Length - trimmed.Length;
                if (indent > 0) block.Inlines.Add(new Run(new string(' ', Math.Min(indent, 4))));

                bool heading = trimmed.StartsWith("#", StringComparison.Ordinal);
                if (heading)
                {
                    trimmed = trimmed.TrimStart('#').TrimStart();
                }
                else if (trimmed.StartsWith("* ", StringComparison.Ordinal) ||
                         trimmed.StartsWith("- ", StringComparison.Ordinal) ||
                         trimmed.StartsWith("+ ", StringComparison.Ordinal))
                {
                    block.Inlines.Add(new Run("•  ") { Foreground = new SolidColorBrush(Color.FromRgb(101, 80, 146)) });
                    trimmed = trimmed.Substring(2);
                }
                else if (trimmed.StartsWith("• ", StringComparison.Ordinal))
                {
                    block.Inlines.Add(new Run("•  ") { Foreground = new SolidColorBrush(Color.FromRgb(101, 80, 146)) });
                    trimmed = trimmed.Substring(2);
                }

                if (heading)
                {
                    var strong = new Bold();
                    AppendStyled(strong.Inlines, trimmed);
                    block.Inlines.Add(strong);
                }
                else
                {
                    AppendStyled(block.Inlines, trimmed);
                }
            }
        }

        private static void AppendStyled(InlineCollection output, string line)
        {
            int offset = 0;
            while (offset < line.Length)
            {
                bool doubleStar = line.IndexOf("**", offset, StringComparison.Ordinal) == offset;
                bool doubleUnder = line.IndexOf("__", offset, StringComparison.Ordinal) == offset;
                if (doubleStar || doubleUnder)
                {
                    string marker = doubleStar ? "**" : "__";
                    int end = line.IndexOf(marker, offset + 2, StringComparison.Ordinal);
                    if (end > offset + 2)
                    {
                        output.Add(new Bold(new Run(line.Substring(offset + 2, end - offset - 2))));
                        offset = end + 2;
                        continue;
                    }
                }
                if (line[offset] == '`')
                {
                    int end = line.IndexOf('`', offset + 1);
                    if (end > offset + 1)
                    {
                        output.Add(new Run(line.Substring(offset + 1, end - offset - 1))
                        {
                            FontFamily = new FontFamily("Consolas"),
                            Background = new SolidColorBrush(Color.FromRgb(234, 227, 244))
                        });
                        offset = end + 1;
                        continue;
                    }
                }
                if (line[offset] == '*' &&
                    (offset == 0 || line[offset - 1] != '*') &&
                    (offset + 1 >= line.Length || line[offset + 1] != '*'))
                {
                    int end = line.IndexOf('*', offset + 1);
                    if (end > offset + 1 && (end + 1 >= line.Length || line[end + 1] != '*'))
                    {
                        output.Add(new Italic(new Run(line.Substring(offset + 1, end - offset - 1))));
                        offset = end + 1;
                        continue;
                    }
                }

                int start = offset;
                offset++;
                while (offset < line.Length && line[offset] != '*' &&
                       line[offset] != '`' && line[offset] != '_')
                    offset++;
                if (offset == start) offset++;
                output.Add(new Run(line.Substring(start, offset - start)));
            }
        }
    }

    public sealed class PositiveCountVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is int && (int)value > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Binding.DoNothing;
        }
    }
}
