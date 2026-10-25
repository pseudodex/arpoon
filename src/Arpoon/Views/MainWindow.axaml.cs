using Avalonia.Controls;

namespace Arpoon
{
    public partial class MainWindow : Window
    {
        private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext;

        // Window-width breakpoint below which the layout switches to a
        // denser, narrower sidebar and smaller type (see Window.compact
        // styles in App.axaml) -- the layout reacts to actual window size
        // instead of just clipping/wrapping at whatever width it's given.
        // Pure view-layout concern, so it stays in code-behind.
        private const double CompactWidthThreshold = 1000;
        private const double SidebarWidthWide = 280;
        private const double SidebarWidthCompact = 220;

        private LogsWindow _logsWindow;

        public MainWindow()
        {
            InitializeComponent();
            DataContext = new MainWindowViewModel();

            SizeChanged += OnMainWindowSizeChanged;
            UpdateResponsiveLayout(Width);
        }

        private void OnMainWindowSizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateResponsiveLayout(e.NewSize.Width);
        }

        private void UpdateResponsiveLayout(double width)
        {
            bool compact = width < CompactWidthThreshold;
            Classes.Set("compact", compact);

            var bodyGrid = this.FindControl<Grid>("BodyGrid");
            if (bodyGrid != null && bodyGrid.ColumnDefinitions.Count > 0)
            {
                bodyGrid.ColumnDefinitions[0].Width = new GridLength(compact ? SidebarWidthCompact : SidebarWidthWide);
            }
        }

        /// <summary>Opens the Logs window, or brings the existing one to front.
        /// Secondary-window lifecycle is left in code-behind -- there's no
        /// navigation-service abstraction in play here, and adding one would
        /// be overkill for a single always-on-top utility window.</summary>
        private void OnShowLogsClick(object sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (_logsWindow == null)
            {
                _logsWindow = new LogsWindow();
                _logsWindow.Closed += (s, args) => _logsWindow = null;
                _logsWindow.Show(this);
            }
            else
            {
                _logsWindow.Activate();
            }
        }

        protected override void OnClosed(System.EventArgs e)
        {
            ViewModel?.Cleanup();
            base.OnClosed(e);
        }
    }
}
