using Avalonia.Media;
using Avalonia.Threading;
using SharpPcap.LibPcap;
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;

namespace Arpoon
{
    /// <summary>
    /// Owns all scan/spoof/throttle/whitelist/global-control orchestration and every
    /// bindable property the main window exposes. The View (<see cref="MainWindow"/>)
    /// is left as near-boilerplate: construct this, set DataContext, and react only to
    /// the handful of things pure XAML binding can't express (responsive layout,
    /// opening the secondary Logs window).
    /// </summary>
    public class MainWindowViewModel : INotifyPropertyChanged
    {
        private CArp _engine;
        private IDeviceIdentifierService _identifierService;

        public ObservableCollection<PC> DetectedPCs { get; } = new();

        public ICommand ScanCommand { get; }
        public ICommand SpoofCommand { get; }

        public MainWindowViewModel()
        {
            ScanCommand = new AsyncRelayCommand(DoScanAsync);
            SpoofCommand = new AsyncRelayCommand(DoSpoofAsync, () => !IsScanning);

            DetectedPCs.CollectionChanged += OnDetectedPcsCollectionChanged;

            // Proactively verify privileges and capabilities
            bool elevated = PrivilegeDetector.IsElevated();
            bool hasCaps = PrivilegeDetector.HasCaptureCapabilities();
            if (!elevated && !hasCaps)
            {
                IsElevationWarningVisible = true;
                ElevationGuidance = PrivilegeDetector.GetElevationGuidance();
            }

            AppDomain.CurrentDomain.ProcessExit += (s, e) => Cleanup();

            try
            {
                System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += (s, e) =>
                {
                    Dispatcher.UIThread.InvokeAsync(ReloadInterfaces);
                };
            }
            catch { }

            ReloadInterfaces();

            if (IsElevationWarningVisible)
            {
                SetStatus("Warning: Superuser privileges or CAP_NET_RAW required.", "warning");
            }
        }

        // ── Global Controls (apply to every controllable device at once) ──
        // Mutually exclusive, mirroring PC.Block/BandwidthLimitKb: turning one
        // on clears the other, both locally and on every device.

        private bool _globalBlockAll = false;
        public bool GlobalBlockAll
        {
            get => _globalBlockAll;
            set
            {
                if (_globalBlockAll == value) return;
                _globalBlockAll = value;
                OnPropertyChanged();
                if (value && _globalCapMbps != 0)
                {
                    _globalCapMbps = 0;
                    OnPropertyChanged(nameof(GlobalCapMbps));
                }
                ApplyGlobalControls();
            }
        }

        private decimal _globalCapMbps = 0;
        public decimal GlobalCapMbps
        {
            get => _globalCapMbps;
            set
            {
                decimal clamped = Math.Max(0m, value);
                if (_globalCapMbps == clamped) return;
                _globalCapMbps = clamped;
                OnPropertyChanged();
                if (clamped > 0 && _globalBlockAll)
                {
                    _globalBlockAll = false;
                    OnPropertyChanged(nameof(GlobalBlockAll));
                }
                ApplyGlobalControls();
            }
        }

        /// <summary>Pushes the current global Block/Cap state onto every
        /// controllable device already in the list.</summary>
        private void ApplyGlobalControls()
        {
            foreach (PC pc in DetectedPCs)
            {
                ApplyGlobalControls(pc);
            }
        }

        /// <summary>Pushes the current global Block/Cap state onto a single
        /// device, e.g. right after it's discovered.</summary>
        private void ApplyGlobalControls(PC pc)
        {
            if (!pc.CanControl) return;

            if (_globalBlockAll)
            {
                pc.Block = true;
            }
            else
            {
                pc.Block = false;
                pc.BandwidthLimitMbps = _globalCapMbps;
            }
        }

        // ── Dashboard Metrics Properties ──

        private int _totalDevicesCount = 0;
        public int TotalDevicesCount
        {
            get => _totalDevicesCount;
            set
            {
                if (_totalDevicesCount != value)
                {
                    _totalDevicesCount = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasNoDevices));
                    OnPropertyChanged(nameof(HasDevices));
                }
            }
        }

        public bool HasNoDevices => _totalDevicesCount == 0;
        public bool HasDevices => _totalDevicesCount > 0;

        private int _spoofedCount = 0;
        public int SpoofedCount
        {
            get => _spoofedCount;
            set { if (_spoofedCount != value) { _spoofedCount = value; OnPropertyChanged(); } }
        }

        private int _blockedCount = 0;
        public int BlockedCount
        {
            get => _blockedCount;
            set { if (_blockedCount != value) { _blockedCount = value; OnPropertyChanged(); } }
        }

        private int _whitelistedCount = 0;
        public int WhitelistedCount
        {
            get => _whitelistedCount;
            set { if (_whitelistedCount != value) { _whitelistedCount = value; OnPropertyChanged(); } }
        }

        // ── Run status indicator (sidebar dot next to "Arpoon") ──
        // Idle: nothing started yet. Running: scan or spoof currently active.
        // Stopped: was running and was explicitly stopped (or disconnected/errored).
        private IBrush _statusDotBrush = new SolidColorBrush(Color.Parse("#5A6472")); // grey = idle
        public IBrush StatusDotBrush
        {
            get => _statusDotBrush;
            set { if (!Equals(_statusDotBrush, value)) { _statusDotBrush = value; OnPropertyChanged(); } }
        }

        private string _statusDotText = "Idle";
        public string StatusDotText
        {
            get => _statusDotText;
            set { if (_statusDotText != value) { _statusDotText = value; OnPropertyChanged(); } }
        }

        // idle=grey (nothing started), scanned=blue (scan completed, not spoofing),
        // spoofing=green (actively intercepting), stopped=red (spoof stopped / error / disconnected)
        private void SetRunStatus(string state)
        {
            string hex = state switch
            {
                "scanned" => "#4A7AA4",
                "spoofing" => "#76A86E",
                "stopped" => "#B05345",
                _ => "#5A6472",
            };
            StatusDotBrush = new SolidColorBrush(Color.Parse(hex));
            StatusDotText = state switch
            {
                "scanned" => "Scan complete",
                "spoofing" => "Spoofing",
                "stopped" => "Stopped",
                _ => "Idle",
            };
        }

        private string _gatewayIpDisplay = "—";
        public string GatewayIpDisplay
        {
            get => _gatewayIpDisplay;
            set { if (_gatewayIpDisplay != value) { _gatewayIpDisplay = value; OnPropertyChanged(); } }
        }

        // ── Gateway Card (SSID/band/link-speed of the adapter being scanned) ──

        private string _gatewayConnectionName = "—";
        public string GatewayConnectionName
        {
            get => _gatewayConnectionName;
            set { if (_gatewayConnectionName != value) { _gatewayConnectionName = value; OnPropertyChanged(); } }
        }

        private string _gatewayBand = "—";
        public string GatewayBand
        {
            get => _gatewayBand;
            set { if (_gatewayBand != value) { _gatewayBand = value; OnPropertyChanged(); } }
        }

        private string _gatewayMaxCapability = "—";
        public string GatewayMaxCapability
        {
            get => _gatewayMaxCapability;
            set { if (_gatewayMaxCapability != value) { _gatewayMaxCapability = value; OnPropertyChanged(); } }
        }

        // ── Warning & Disclaimer Banner Properties ──

        private bool _isElevationWarningVisible;
        public bool IsElevationWarningVisible
        {
            get => _isElevationWarningVisible;
            set { if (_isElevationWarningVisible != value) { _isElevationWarningVisible = value; OnPropertyChanged(); } }
        }

        private string _elevationGuidance = string.Empty;
        public string ElevationGuidance
        {
            get => _elevationGuidance;
            set { if (_elevationGuidance != value) { _elevationGuidance = value; OnPropertyChanged(); } }
        }

        // ── Status pill (message + semantic color), formerly set by reaching
        // into named controls from code-behind -- now plain bound properties. ──

        private string _statusMessage = string.Empty;
        public string StatusMessage
        {
            get => _statusMessage;
            set { if (_statusMessage != value) { _statusMessage = value; OnPropertyChanged(); } }
        }

        private IBrush _statusPillDotBrush = new SolidColorBrush(Color.Parse("#4A7AA4"));
        public IBrush StatusPillDotBrush
        {
            get => _statusPillDotBrush;
            set { if (!Equals(_statusPillDotBrush, value)) { _statusPillDotBrush = value; OnPropertyChanged(); } }
        }

        private IBrush _statusPillBackgroundBrush = new SolidColorBrush(Color.Parse("#1C2127"));
        public IBrush StatusPillBackgroundBrush
        {
            get => _statusPillBackgroundBrush;
            set { if (!Equals(_statusPillBackgroundBrush, value)) { _statusPillBackgroundBrush = value; OnPropertyChanged(); } }
        }

        // ── Network interface selection ──

        private ObservableCollection<NetworkInterfaceOption> _networkInterfaceOptions = new();
        public ObservableCollection<NetworkInterfaceOption> NetworkInterfaceOptions
        {
            get => _networkInterfaceOptions;
            private set { _networkInterfaceOptions = value; OnPropertyChanged(); }
        }

        private NetworkInterfaceOption _selectedInterfaceOption;
        public NetworkInterfaceOption SelectedInterfaceOption
        {
            get => _selectedInterfaceOption;
            set { if (_selectedInterfaceOption != value) { _selectedInterfaceOption = value; OnPropertyChanged(); } }
        }

        // ── Spoof button state (was read off the Button's own CSS classes) ──

        private bool _isSpoofingActive;
        public bool IsSpoofingActive
        {
            get => _isSpoofingActive;
            set
            {
                if (_isSpoofingActive != value)
                {
                    _isSpoofingActive = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(SpoofButtonText));
                }
            }
        }

        public string SpoofButtonText => IsSpoofingActive ? "Stop" : "Spoof";

        private bool _isScanning;
        private bool IsScanning
        {
            get => _isScanning;
            set
            {
                if (_isScanning != value)
                {
                    _isScanning = value;
                    (SpoofCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        private void ReloadInterfaces()
        {
            try
            {
                var devices = SharpPcap.CaptureDeviceList.Instance;
                var validDevices = devices.OfType<LibPcapLiveDevice>().Where(d => d.Addresses.Any(a =>
                    a.Addr.ipAddress != null &&
                    a.Addr.ipAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)).ToList();

                var options = NetworkInterfaceOption.BuildOptions(validDevices);
                NetworkInterfaceOptions = new ObservableCollection<NetworkInterfaceOption>(options);

                if (options.Any())
                {
                    SelectedInterfaceOption = options[0];
                    SetStatus($"{options.Count} interface(s) detected.", "success");
                }
                else
                {
                    SelectedInterfaceOption = null;
                    SetStatus("No valid network interfaces detected.", "warning");
                }
            }
            catch (Exception ex)
            {
                SetStatus($"Error loading interfaces: {ex.Message}", "error");
            }
        }

        private void OnDetectedPcsCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
            {
                foreach (PC pc in e.NewItems)
                {
                    pc.PropertyChanged += OnPcPropertyChanged;
                    if (pc.IsGateway && pc.Ip != null)
                    {
                        GatewayIpDisplay = pc.Ip.ToString();
                    }
                    // Newly discovered devices inherit whatever global
                    // Block/Cap is currently active.
                    ApplyGlobalControls(pc);
                }
            }

            if (e.OldItems != null)
            {
                foreach (PC pc in e.OldItems)
                {
                    pc.PropertyChanged -= OnPcPropertyChanged;
                }
            }

            RecalculateDashboardMetrics();
        }

        private void OnPcPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // A device removed from the whitelist picks up the active global state.
            if (e.PropertyName == nameof(PC.IsWhitelisted) && sender is PC wl && !wl.IsWhitelisted)
            {
                ApplyGlobalControls(wl);
            }
            if (e.PropertyName == nameof(PC.Redirect) ||
                e.PropertyName == nameof(PC.Block) ||
                e.PropertyName == nameof(PC.IsWhitelisted) ||
                e.PropertyName == nameof(PC.IsGateway))
            {
                RecalculateDashboardMetrics();
            }
        }

        private void RecalculateDashboardMetrics()
        {
            TotalDevicesCount = DetectedPCs.Count;
            SpoofedCount = DetectedPCs.Count(p => p.Redirect);
            BlockedCount = DetectedPCs.Count(p => p.Block);
            WhitelistedCount = DetectedPCs.Count(p => p.IsWhitelisted);

            var gateway = DetectedPCs.FirstOrDefault(p => p.IsGateway);
            if (gateway?.Ip != null)
            {
                GatewayIpDisplay = gateway.Ip.ToString();
            }
        }

        /// <summary>Shells out (off the UI thread -- these OS tools can take a
        /// moment) to populate the Gateway card's SSID/band/link-speed for the
        /// adapter being scanned. Best-effort: see <see cref="GatewayLinkInfoService"/>.</summary>
        private void RefreshGatewayLinkInfo(NetworkInterfaceOption option)
        {
            _ = Task.Run(() =>
            {
                var info = GatewayLinkInfoService.GetLinkInfo(option);
                Dispatcher.UIThread.InvokeAsync(() =>
                {
                    GatewayConnectionName = info.ConnectionName ?? "—";
                    GatewayBand = info.Band ?? "—";
                    GatewayMaxCapability = info.MaxCapability ?? "—";
                });
            });
        }

        private async Task DoScanAsync()
        {
            var selectedOption = SelectedInterfaceOption;
            var device = selectedOption?.Device;
            if (device == null)
            {
                SetStatus("Select a network interface first.", "warning");
                return;
            }

            IsScanning = true;
            try
            {
                // If engine exists, stop previous operations asynchronously
                if (_engine != null || _identifierService != null)
                {
                    SetStatus("Stopping previous scan and releasing network resources...", "warning");
                    var oldEngine = _engine;
                    var oldIdService = _identifierService;
                    if (oldEngine != null)
                    {
                        oldEngine.OnInterfaceDisconnected -= OnEngineInterfaceDisconnected;
                    }
                    _engine = null;
                    _identifierService = null;

                    await Task.Run(() =>
                    {
                        try
                        {
                            oldIdService?.StopMdnsListener();
                            oldIdService?.Dispose();
                            oldEngine?.StopArpListener();
                            oldEngine?.StopDiscovery();
                            oldEngine?.StopSpoofing();
                            oldEngine?.Dispose();
                        }
                        catch (Exception ex)
                        {
                            Logger.Write($"[CLEANUP ERROR] {ex.Message}");
                        }
                    });

                    IsSpoofingActive = false;
                }

                DetectedPCs.Clear();
                GatewayIpDisplay = "—";
                GatewayConnectionName = "—";
                GatewayBand = "—";
                GatewayMaxCapability = "—";
                RefreshGatewayLinkInfo(selectedOption);

                try
                {
                    SetStatus("Initializing network scan...", "info");

                    var deviceList = new PcList();
                    deviceList.SetOnDeviceAdded((newPc) =>
                    {
                        Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            DetectedPCs.Add(newPc);
                            SetStatus($"{DetectedPCs.Count} device(s) detected on the network.", "success");
                            SetRunStatus("scanned");
                        });
                    });

                    CArp newEngine = null;
                    IDeviceIdentifierService newIdService = null;

                    await Task.Run(() =>
                    {
                        newEngine = new CArp(device, deviceList);
                        newIdService = new DeviceIdentifierService(device, deviceList);
                        deviceList.SetIdentifierService(newIdService);

                        newEngine.StartArpListener();
                        newEngine.StartArpDiscovery();
                        newIdService.StartMdnsListener();
                    });

                    _engine = newEngine;
                    _identifierService = newIdService;
                    _engine.OnInterfaceDisconnected += OnEngineInterfaceDisconnected;

                    // Record the detected gateway for the Overview card.
                    string routerIp = _engine.RouterIp != null ? new System.Net.IPAddress(_engine.RouterIp).ToString() : "N/A";
                    if (!string.IsNullOrEmpty(routerIp) && routerIp != "N/A")
                    {
                        GatewayIpDisplay = routerIp;
                    }

                    var selectedName = SelectedInterfaceOption?.DisplayName ?? device.Name;
                    SetStatus($"Active scan on {selectedName}", "info");
                }
                catch (Exception ex)
                {
                    SetStatus($"Error starting scan: {ex.Message}", "error");
                    SetRunStatus("stopped");
                    Logger.Write($"[SCAN ERROR] {ex}");
                }
            }
            finally
            {
                IsScanning = false;
            }
        }

        private async Task DoSpoofAsync()
        {
            if (!PrivilegeDetector.HasCaptureCapabilities())
            {
                SetStatus("Cannot start spoof: superuser (root/admin) or CAP_NET_RAW required.", "error");
                IsElevationWarningVisible = true;
                return;
            }

            if (_engine == null)
            {
                SetStatus("Scan the network before starting spoof.", "warning");
                return;
            }

            bool isStarting = !IsSpoofingActive;

            if (isStarting)
            {
                if (_engine.RouterMac == null)
                {
                    SetStatus("Router MAC not detected. Scan again.", "error");
                    return;
                }

                await Task.Run(() => _engine.StartSpoofing());

                IsSpoofingActive = true;
                SetStatus("ARP Spoof active — intercepting traffic.", "error");
                SetRunStatus("spoofing");
            }
            else
            {
                SetStatus("Stopping spoof and restoring network tables...", "warning");
                await Task.Run(() => _engine.StopSpoofing());

                IsSpoofingActive = false;
                SetStatus("Spoof stopped. Network restored.", "success");
                SetRunStatus("stopped");
            }
        }

        /// <summary>
        /// Updates the status pill's message and semantic color (success, info, warning, error).
        /// Thread-safe: can be called from any thread.
        /// </summary>
        private void SetStatus(string message, string level = "info")
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.InvokeAsync(() => SetStatus(message, level));
                return;
            }

            Logger.FromStatus(message, level);
            StatusMessage = message;

            Color dotColor;
            Color borderBg;
            switch (level.ToLowerInvariant())
            {
                case "success":
                    dotColor = Color.Parse("#76A86E");
                    borderBg = Color.Parse("#1C2127");
                    break;
                case "warning":
                    dotColor = Color.Parse("#CEA65C");
                    borderBg = Color.Parse("#1C2127");
                    break;
                case "error":
                    dotColor = Color.Parse("#B05345");
                    borderBg = Color.Parse("#271D1B");
                    break;
                case "info":
                default:
                    dotColor = Color.Parse("#4A7AA4");
                    borderBg = Color.Parse("#1C2127");
                    break;
            }

            StatusPillDotBrush = new SolidColorBrush(dotColor);
            StatusPillBackgroundBrush = new SolidColorBrush(borderBg);
        }

        private void OnEngineInterfaceDisconnected(string reason)
        {
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                SetStatus($"Network disconnected: {reason}", "error");
                SetRunStatus("stopped");
                IsSpoofingActive = false;

                ReloadInterfaces();
            });
        }

        public void Cleanup()
        {
            try
            {
                if (_engine != null)
                {
                    _engine.OnInterfaceDisconnected -= OnEngineInterfaceDisconnected;
                }
                _identifierService?.StopMdnsListener();
                _identifierService?.Dispose();
                _identifierService = null;
                _engine?.Dispose();
                _engine = null;
            }
            catch { }
        }

        // ── INotifyPropertyChanged ──

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
