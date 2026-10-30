using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using SharpPcap.LibPcap;

namespace Arpoon
{
    /// <summary>
    /// Broad category used to pick an icon for an interface in the UI.
    /// </summary>
    public enum NetworkInterfaceKind
    {
        Ethernet,
        WiFi,
        Other
    }

    /// <summary>
    /// Wraps a raw SharpPcap capture device with a human-readable name (e.g. "Wi-Fi",
    /// "Ethernet") resolved from the OS network stack, instead of the opaque device
    /// name/GUID that libpcap exposes (e.g. "\Device\NPF_{4D36E972-...}" on Windows).
    /// </summary>
    public class NetworkInterfaceOption
    {
        public LibPcapLiveDevice Device { get; }
        public string DisplayName { get; }
        public NetworkInterfaceKind Kind { get; }

        /// <summary>The OS network stack's own identifier for this adapter
        /// (<see cref="NetworkInterface.Id"/>) -- on Linux/macOS this is the
        /// BSD/ifname ("wlan0", "en0"), which is what tools like iw/nmcli/
        /// airport expect, as opposed to <see cref="DisplayName"/> which is
        /// the human-friendly name. Null when no OS interface could be
        /// matched (see <see cref="GatewayLinkInfoService"/>, which falls
        /// back to DisplayName in that case).</summary>
        public string OsInterfaceId { get; }

        public NetworkInterfaceOption(LibPcapLiveDevice device, string displayName, NetworkInterfaceKind kind, string osInterfaceId = null)
        {
            Device = device;
            DisplayName = displayName;
            Kind = kind;
            OsInterfaceId = osInterfaceId;
        }

        /// <summary>
        /// Builds display-friendly options for a set of pcap devices by matching each one
        /// against the OS-reported network interfaces (by IPv4 address), falling back to
        /// the pcap description/name when no match is found.
        /// </summary>
        public static List<NetworkInterfaceOption> BuildOptions(IEnumerable<LibPcapLiveDevice> devices)
        {
            var osInterfaces = SafeGetOsInterfaces();
            var options = new List<NetworkInterfaceOption>();

            foreach (var device in devices)
            {
                var match = FindMatchingOsInterface(device, osInterfaces);

                // Only offer interfaces that actually have an IPv4 address to work
                // with. On macOS SharpPcap never reports one (see
                // FindMatchingOsInterface), so this relies on the OS-matched
                // interface; on Windows/Linux either source is accepted.
                if (!((match != null && InterfaceHasIPv4(match)) || DeviceHasIPv4(device)))
                {
                    continue;
                }

                string name;
                NetworkInterfaceKind kind;

                if (match != null)
                {
                    name = match.Name;
                    kind = ClassifyKind(match.NetworkInterfaceType, name);
                }
                else
                {
                    name = !string.IsNullOrWhiteSpace(device.Description) ? device.Description : device.Name;
                    kind = ClassifyKind(null, name);
                }

                // Arpoon only targets Wi-Fi and wired LAN; skip anything else
                // (virtual/tunnel/Bluetooth/etc. adapters that would otherwise
                // clutter the dropdown with options that don't apply here).
                if (kind != NetworkInterfaceKind.WiFi && kind != NetworkInterfaceKind.Ethernet)
                {
                    continue;
                }

                options.Add(new NetworkInterfaceOption(device, name, kind, match?.Id));
            }

            return options;
        }

        /// <summary>
        /// Resolves the OS network interface backing a pcap device (by name/id, then
        /// IPv4). Exposed so the ARP engine can obtain the local IPv4/netmask from the
        /// OS stack on platforms (macOS) where SharpPcap fails to parse them.
        /// </summary>
        public static NetworkInterface ResolveOsInterface(LibPcapLiveDevice device)
        {
            if (device == null)
            {
                return null;
            }

            return FindMatchingOsInterface(device, SafeGetOsInterfaces());
        }

        private static bool InterfaceHasIPv4(NetworkInterface ni)
        {
            try
            {
                return ni.GetIPProperties().UnicastAddresses
                    .Any(u => u.Address.AddressFamily == AddressFamily.InterNetwork);
            }
            catch
            {
                return false;
            }
        }

        private static bool DeviceHasIPv4(LibPcapLiveDevice device)
        {
            try
            {
                return device.Addresses.Any(a => a?.Addr?.ipAddress != null &&
                    a.Addr.ipAddress.AddressFamily == AddressFamily.InterNetwork);
            }
            catch
            {
                return false;
            }
        }

        private static List<NetworkInterface> SafeGetOsInterfaces()
        {
            try
            {
                return NetworkInterface.GetAllNetworkInterfaces()
                    .Where(ni => ni.OperationalStatus == OperationalStatus.Up)
                    .ToList();
            }
            catch
            {
                return new List<NetworkInterface>();
            }
        }

        private static NetworkInterface FindMatchingOsInterface(LibPcapLiveDevice device, List<NetworkInterface> osInterfaces)
        {
            if (osInterfaces.Count == 0)
            {
                return null;
            }

            // Primary match: by interface identifier. On Linux/macOS the pcap device
            // name IS the BSD ifname (e.g. "en0", "eth0"), identical to
            // NetworkInterface.Name. On Windows the pcap name embeds the adapter GUID
            // (e.g. "\Device\NPF_{GUID}"), which equals NetworkInterface.Id. This is the
            // only reliable path on macOS, where SharpPcap cannot parse the BSD sockaddr
            // and every device.Addresses[].Addr.ipAddress comes back null.
            if (!string.IsNullOrEmpty(device.Name))
            {
                foreach (var ni in osInterfaces)
                {
                    if (string.Equals(device.Name, ni.Name, StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrEmpty(ni.Id) &&
                         device.Name.IndexOf(ni.Id, StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        return ni;
                    }
                }
            }

            // Secondary match: by IPv4 address (works where SharpPcap parses addresses).
            var deviceIPs = new HashSet<IPAddress>();
            try
            {
                foreach (var addr in device.Addresses)
                {
                    if (addr?.Addr?.ipAddress != null)
                    {
                        deviceIPs.Add(addr.Addr.ipAddress);
                    }
                }
            }
            catch
            {
                // Some adapters throw when enumerating addresses mid-scan; just skip matching.
            }

            if (deviceIPs.Count == 0)
            {
                return null;
            }

            foreach (var ni in osInterfaces)
            {
                try
                {
                    var niIPs = ni.GetIPProperties().UnicastAddresses.Select(u => u.Address);
                    if (niIPs.Any(ip => deviceIPs.Contains(ip)))
                    {
                        return ni;
                    }
                }
                catch
                {
                    // Ignore adapters whose properties can't be read.
                }
            }

            return null;
        }

        private static NetworkInterfaceKind ClassifyKind(NetworkInterfaceType? osType, string name)
        {
            if (osType != null)
            {
                switch (osType)
                {
                    case NetworkInterfaceType.Wireless80211:
                        return NetworkInterfaceKind.WiFi;
                    case NetworkInterfaceType.Ethernet:
                    case NetworkInterfaceType.GigabitEthernet:
                    case NetworkInterfaceType.FastEthernetT:
                    case NetworkInterfaceType.FastEthernetFx:
                        return NetworkInterfaceKind.Ethernet;
                    default:
                        return NetworkInterfaceKind.Other;
                }
            }

            // Fallback heuristic from the adapter's name/description when the OS lookup fails.
            var lower = (name ?? string.Empty).ToLowerInvariant();
            if (lower.Contains("wi-fi") || lower.Contains("wifi") || lower.Contains("wlan") || lower.Contains("wireless"))
            {
                return NetworkInterfaceKind.WiFi;
            }
            if (lower.Contains("eth") || lower.Contains("lan") || lower.Contains("ethernet"))
            {
                return NetworkInterfaceKind.Ethernet;
            }
            return NetworkInterfaceKind.Other;
        }
    }
}
