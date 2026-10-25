using Avalonia.Controls;
using System.Collections.Specialized;

namespace Arpoon
{
    public partial class LogsWindow : Window
    {
        private LogsWindowViewModel ViewModel => (LogsWindowViewModel)DataContext;

        private readonly ListBox _listBox;

        public LogsWindow()
        {
            InitializeComponent();

            var viewModel = new LogsWindowViewModel();
            DataContext = viewModel;

            _listBox = this.FindControl<ListBox>("LogListBox");

            // Auto-scroll-to-latest-entry needs a direct ListBox reference
            // (ScrollIntoView), which plain XAML binding can't express --
            // the one deliberate exception to "no logic in code-behind" here.
            viewModel.Entries.CollectionChanged += OnEntriesChanged;
            ScrollToEnd();

            Closed += (s, e) =>
            {
                viewModel.Entries.CollectionChanged -= OnEntriesChanged;
                viewModel.Detach();
            };
        }

        private void OnEntriesChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Add)
            {
                ScrollToEnd();
            }
        }

        private void ScrollToEnd()
        {
            if (ViewModel?.AutoScroll == true && _listBox != null && ViewModel.Entries.Count > 0)
            {
                _listBox.ScrollIntoView(ViewModel.Entries[^1]);
            }
        }
    }
}
