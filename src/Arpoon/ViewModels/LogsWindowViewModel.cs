using Avalonia.Threading;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Arpoon
{
    /// <summary>Live, verbose view of <see cref="Logger"/>'s entries. Non-modal: can
    /// stay open while scanning/spoofing continues, and keeps appending until closed.</summary>
    public class LogsWindowViewModel : INotifyPropertyChanged
    {
        public ObservableCollection<LogEntry> Entries { get; } = new();

        public ICommand ClearCommand { get; }

        private bool _autoScroll = true;
        public bool AutoScroll
        {
            get => _autoScroll;
            set { if (_autoScroll != value) { _autoScroll = value; OnPropertyChanged(); } }
        }

        private string _countText = "0 entries";
        public string CountText
        {
            get => _countText;
            set { if (_countText != value) { _countText = value; OnPropertyChanged(); } }
        }

        public LogsWindowViewModel()
        {
            ClearCommand = new RelayCommand(Clear);

            foreach (LogEntry entry in Logger.Snapshot())
            {
                Entries.Add(entry);
            }
            UpdateCount();

            Logger.EntryAdded += OnEntryAdded;
        }

        private void OnEntryAdded(LogEntry entry)
        {
            Dispatcher.UIThread.Post(() =>
            {
                Entries.Add(entry);
                UpdateCount();
            });
        }

        private void Clear()
        {
            Logger.Clear();
            Entries.Clear();
            UpdateCount();
        }

        private void UpdateCount()
        {
            CountText = $"{Entries.Count} entries";
        }

        public void Detach()
        {
            Logger.EntryAdded -= OnEntryAdded;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
