using System;
using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Threading;
using Avalonia.Threading;

namespace Arpoon
{
    public class PC : INotifyPropertyChanged
    {
        // ── Display properties (XAML bindings) ──

        public string IpDisplay => Ip?.ToString() ?? "—";
        public string MacDisplay => Mac?.ToString() ?? "—";

        /// <summary>
        /// Formatted colon-delimited MAC address (e.g. "74:D8:3E:6E:1B:C6") for tabular readability.
        /// </summary>
        public string MacFormatted
        {
            get
            {
                if (Mac == null) return "—";
                string macStr = Mac.ToString();
                if (macStr.Length == 12)
                {
                    return $"{macStr[0..2]}:{macStr[2..4]}:{macStr[4..6]}:{macStr[6..8]}:{macStr[8..10]}:{macStr[10..12]}";
                }
                return macStr;
            }
        }

        // ── Core network properties ──

        private IPAddress _ip;
        public IPAddress Ip
        {
            get => _ip;
            set { if (_ip != value) { _ip = value; OnPropertyChanged(); OnPropertyChanged(nameof(IpDisplay)); } }
        }

        private PhysicalAddress _mac;
        public PhysicalAddress Mac
        {
            get => _mac;
            set { if (_mac != value) { _mac = value; OnPropertyChanged(); OnPropertyChanged(nameof(MacDisplay)); OnPropertyChanged(nameof(MacFormatted)); OnPropertyChanged(nameof(DeviceLabel)); OnPropertyChanged(nameof(CanToggleWhitelist)); if (_mac != null && WhitelistStore.Contains(_mac.ToString())) ApplyWhitelist(true); } }
        }

        public string Name { get; set; } = "Unknown";

        // ── Device identification ──

        private string _vendor;
        public string Vendor
        {
            get => _vendor;
            set
            {
                if (_vendor != value)
                {
                    _vendor = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DeviceLabel));
                    OnPropertyChanged(nameof(IsRandomizedMac));
                }
            }
        }

        /// <summary>True when the MAC's locally-administered bit is set and no
        /// real vendor could be resolved (see <see cref="OuiDatabase.IsRandomizedMac"/>) --
        /// shown as a badge in the table instead of baked into the device label text.</summary>
        public bool IsRandomizedMac => string.Equals(_vendor, "Randomized MAC", StringComparison.Ordinal);

        private string _hostname;
        public string Hostname
        {
            get => _hostname;
            set { if (_hostname != value) { _hostname = value; OnPropertyChanged(); OnPropertyChanged(nameof(DeviceLabel)); } }
        }

        private DeviceType _deviceCategory = DeviceType.Unknown;
        public DeviceType DeviceCategory
        {
            get => _deviceCategory;
            set { if (_deviceCategory != value) { _deviceCategory = value; OnPropertyChanged(); OnPropertyChanged(nameof(DeviceLabel)); OnPropertyChanged(nameof(CategoryName)); } }
        }

        public string CategoryName => _deviceCategory != DeviceType.Unknown ? _deviceCategory.ToString() : "Device";

        /// <summary>
        /// Computed display label for UI: combines device type, vendor, and hostname.
        /// Falls back to formatted MAC address when no identification is available.
        /// </summary>
        public string DeviceLabel
        {
            get
            {
                if (IsLocalPc)
                {
                    string localHostName = !string.IsNullOrEmpty(_hostname) &&
                                          !_hostname.EndsWith(".arpa", StringComparison.OrdinalIgnoreCase) &&
                                          !IPAddress.TryParse(_hostname, out _)
                                          ? _hostname.Trim()
                                          : Environment.MachineName;

                    return TruncateWithEllipsis($"{localHostName} (Local Host)", 55);
                }

                string type = _deviceCategory != DeviceType.Unknown ? _deviceCategory.ToString() : null;
                string name = !string.IsNullOrEmpty(_hostname) && !_hostname.EndsWith(".arpa", StringComparison.OrdinalIgnoreCase) ? _hostname.Trim() : null;
                bool isRandomized = string.Equals(_vendor, "Randomized MAC", StringComparison.Ordinal);
                string vendor = !string.IsNullOrEmpty(_vendor) && !isRandomized ? _vendor.Trim() : null;

                bool vendorRedundant = false;
                if (name != null && vendor != null)
                {
                    if (name.Contains(vendor, StringComparison.OrdinalIgnoreCase) ||
                        vendor.Contains(name, StringComparison.OrdinalIgnoreCase))
                    {
                        vendorRedundant = true;
                    }
                    else
                    {
                        var tokens = vendor.Split(new[] { ' ', ',', '-', '.', '_' }, StringSplitOptions.RemoveEmptyEntries);
                        if (tokens.Length > 0 && tokens[0].Length >= 3 && name.Contains(tokens[0], StringComparison.OrdinalIgnoreCase))
                        {
                            vendorRedundant = true;
                        }
                    }
                }

                string rawLabel;
                if (type != null && name != null && vendor != null)
                {
                    if (vendorRedundant)
                        rawLabel = $"{type} — {name}";
                    else
                        rawLabel = $"{type} — {name} ({vendor})";
                }
                else if (type != null && name != null)
                {
                    rawLabel = $"{type} — {name}";
                }
                else if (type != null && vendor != null)
                {
                    rawLabel = $"{type} ({vendor})";
                }
                else if (name != null && vendor != null)
                {
                    if (vendorRedundant)
                        rawLabel = name;
                    else
                        rawLabel = $"{name} ({vendor})";
                }
                else if (name != null)
                {
                    rawLabel = name;
                }
                else if (vendor != null)
                {
                    rawLabel = vendor;
                }
                else if (Mac != null)
                {
                    string macStr = Mac.ToString();
                    if (macStr.Length == 12)
                    {
                        rawLabel = $"{macStr[0..2]}:{macStr[2..4]}:{macStr[4..6]}:{macStr[6..8]}:{macStr[8..10]}:{macStr[10..12]}";
                    }
                    else
                    {
                        rawLabel = "Unknown Device";
                    }
                }
                else
                {
                    rawLabel = "Unknown Device";
                }

                return TruncateWithEllipsis(rawLabel, 55);
            }
        }

        private static string TruncateWithEllipsis(string value, int maxLen = 55)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLen) return value;
            return string.Concat(value.AsSpan(0, maxLen - 1), "…");
        }

        private bool _redirect = false;
        public bool Redirect
        {
            get => _redirect;
            set
            {
                if (CanControl && _redirect != value)
                {
                    _redirect = value;
                    OnPropertyChanged();
                    if (_redirect && _block)
                    {
                        _block = false;
                        OnPropertyChanged(nameof(Block));
                        OnPropertyChanged(nameof(IsThrottlingEnabled));
                    }
                }
            }
        }

        private bool _block = false;
        public bool Block
        {
            get => _block;
            set
            {
                if (CanControl && _block != value)
                {
                    _block = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsThrottlingEnabled));
                    if (_block)
                    {
                        // Blocking drops all traffic outright, so there's nothing
                        // left to cap or redirect -- clear both.
                        Redirect = false;
                        BandwidthLimitKb = 0;
                    }
                }
            }
        }

        private int _bandwidthLimitKb = 0;
        /// <summary>Allowed bandwidth limit in KB/s (0 = Unlimited) -- the unit the
        /// throttlers actually operate in (see Throttling/*.cs). The UI shows and
        /// edits this as <see cref="BandwidthLimitMbps"/> instead, since Mbps is
        /// the unit internet speeds are normally quoted in. There's no separate
        /// "enable throttling" toggle in the table -- setting a cap above 0 turns
        /// on <see cref="Redirect"/> itself (traffic has to be intercepted for a
        /// cap to mean anything), and dropping the cap back to 0 turns it off.</summary>
        public int BandwidthLimitKb
        {
            get => _bandwidthLimitKb;
            set
            {
                int clamped = Math.Max(0, value);
                if (_bandwidthLimitKb != clamped)
                {
                    _bandwidthLimitKb = clamped;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(BandwidthLimitDisplay));
                    OnPropertyChanged(nameof(BandwidthLimitMbps));
                    Redirect = clamped > 0;
                }
            }
        }

        /// <summary>Bindable Mbps view of <see cref="BandwidthLimitKb"/> for the
        /// table's cap control. Conversion matches the throttlers' own KB/s-to-
        /// kbit/s rule (1 KB/s = 8 kbit/s), so 1 Mbps = 125 KB/s.</summary>
        public decimal BandwidthLimitMbps
        {
            get => Math.Round(_bandwidthLimitKb * 8m / 1000m, 2);
            set
            {
                BandwidthLimitKb = (int)Math.Round(Math.Max(0m, value) * 1000m / 8m, MidpointRounding.AwayFromZero);
                OnPropertyChanged();
            }
        }

        public string BandwidthLimitDisplay => _bandwidthLimitKb == 0 ? "Unlimited" : $"{BandwidthLimitMbps} Mbps";

        private bool _isGateway;
        public bool IsGateway
        {
            get => _isGateway;
            set
            {
                if (_isGateway != value)
                {
                    _isGateway = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CanControl));
                    OnPropertyChanged(nameof(CanToggleWhitelist));
                    OnPropertyChanged(nameof(IsThrottlingEnabled));
                    OnPropertyChanged(nameof(HasRoleBadge));
                    OnPropertyChanged(nameof(RoleBadgeText));
                }
            }
        }

        private bool _isLocalPc;
        public bool IsLocalPc
        {
            get => _isLocalPc;
            set
            {
                if (_isLocalPc != value)
                {
                    _isLocalPc = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CanControl));
                    OnPropertyChanged(nameof(CanToggleWhitelist));
                    OnPropertyChanged(nameof(IsThrottlingEnabled));
                    OnPropertyChanged(nameof(HasRoleBadge));
                    OnPropertyChanged(nameof(RoleBadgeText));
                }
            }
        }

        private bool _isWhitelisted;
        /// <summary>Whitelisted devices are never capped, blocked or intercepted,
        /// including by the global controls. Persisted by MAC via <see cref="WhitelistStore"/>.</summary>
        public bool IsWhitelisted
        {
            get => _isWhitelisted;
            set
            {
                if (_isWhitelisted == value) return;
                if (Mac != null) WhitelistStore.Set(Mac.ToString(), value);
                ApplyWhitelist(value);
            }
        }

        public bool CanToggleWhitelist => !IsLocalPc && !IsGateway && Mac != null;
        public string WhitelistToolTip => _isWhitelisted ? "Whitelisted — click to remove" : "Add to whitelist";

        private void ApplyWhitelist(bool value)
        {
            if (_isWhitelisted == value) return;
            if (value)
            {
                // Clear any active restrictions before locking controls out.
                BandwidthLimitKb = 0;
                Block = false;
                Redirect = false;
            }
            _isWhitelisted = value;
            OnPropertyChanged(nameof(IsWhitelisted));
            OnPropertyChanged(nameof(CanControl));
            OnPropertyChanged(nameof(IsThrottlingEnabled));
            OnPropertyChanged(nameof(WhitelistToolTip));
        }

        public bool HasRoleBadge => IsLocalPc || IsGateway;
        public string RoleBadgeText => IsLocalPc ? "YOU" : (IsGateway ? "GATEWAY" : "");

        public bool CanControl => !IsLocalPc && !IsGateway && !IsWhitelisted;

        // Gates the Cap control itself: anything controllable and not currently
        // Blocked can have a cap set (setting one is what turns Redirect on).
        public bool IsThrottlingEnabled => CanControl && !_block;

        // ── Tracking ──

        public DateTime TimeSinceLastArp { get; set; }

        // ── INotifyPropertyChanged ──

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            var handler = PropertyChanged;
            if (handler == null) return;

            if (Dispatcher.UIThread != null && !Dispatcher.UIThread.CheckAccess())
            {
                try
                {
                    Dispatcher.UIThread.Post(() => handler(this, new PropertyChangedEventArgs(propertyName)), DispatcherPriority.Background);
                }
                catch
                {
                    handler(this, new PropertyChangedEventArgs(propertyName));
                }
            }
            else
            {
                handler(this, new PropertyChangedEventArgs(propertyName));
            }
        }
    }
}