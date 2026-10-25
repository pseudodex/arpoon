using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Arpoon
{
    /// <summary>Best-effort detection of the active adapter's SSID/band/link-
    /// speed, by shelling out to the OS's own network tooling (there's no
    /// portable .NET API for this). Every path is wrapped so a missing tool,
    /// an unexpected output format, or a slow/hanging process never takes
    /// down the caller -- it just falls back to <see cref="GatewayLinkInfo.Unavailable"/>.</summary>
    public static class GatewayLinkInfoService
    {
        public static GatewayLinkInfo GetLinkInfo(NetworkInterfaceOption option)
        {
            if (option == null) return GatewayLinkInfo.Unavailable();

            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return GetWindows(option);
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return GetMac(option);
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return GetLinux(option);
            }
            catch (Exception ex)
            {
                Logger.Write($"[GATEWAY INFO] Detection failed: {ex.Message}");
            }

            return GatewayLinkInfo.Unavailable(option.DisplayName, option.Kind == NetworkInterfaceKind.WiFi);
        }

        // ── Linux: iw (preferred) / nmcli (fallback) for Wi-Fi, ethtool for wired ──

        private static GatewayLinkInfo GetLinux(NetworkInterfaceOption option)
        {
            string ifname = option.OsInterfaceId ?? option.DisplayName;
            bool isWireless = option.Kind == NetworkInterfaceKind.WiFi;

            if (isWireless)
            {
                string iwOutput = RunCommand("iw", $"dev {ifname} link");
                if (!string.IsNullOrWhiteSpace(iwOutput) && !iwOutput.Contains("Not connected"))
                {
                    var ssidMatch = Regex.Match(iwOutput, @"SSID:\s*(.+)");
                    var freqMatch = Regex.Match(iwOutput, @"freq:\s*(\d+)");
                    var rateMatch = Regex.Match(iwOutput, @"tx bitrate:\s*([\d.]+)\s*MBit/s");

                    if (ssidMatch.Success)
                    {
                        return new GatewayLinkInfo
                        {
                            ConnectionName = ssidMatch.Groups[1].Value.Trim(),
                            IsWireless = true,
                            Band = freqMatch.Success ? BandFromFrequencyMHz(int.Parse(freqMatch.Groups[1].Value)) : null,
                            MaxCapability = rateMatch.Success ? FormatMbps(double.Parse(rateMatch.Groups[1].Value)) : null
                        };
                    }
                }

                // nmcli fallback: list visible networks and pick the active one.
                string nmcliOutput = RunCommand("nmcli", "-t -f ACTIVE,SSID,CHAN,RATE dev wifi");
                foreach (var line in (nmcliOutput ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var fields = line.Split(':');
                    if (fields.Length < 4 || fields[0] != "yes") continue;

                    string rateText = fields[3].Replace("Mbit/s", "").Trim();
                    return new GatewayLinkInfo
                    {
                        ConnectionName = fields[1],
                        IsWireless = true,
                        Band = int.TryParse(fields[2], out int chan) ? BandFromChannel(chan) : null,
                        MaxCapability = double.TryParse(rateText, out double rate) ? FormatMbps(rate) : null
                    };
                }

                return GatewayLinkInfo.Unavailable(ifname, true);
            }
            else
            {
                string connName = RunCommand("nmcli", $"-t -f GENERAL.CONNECTION device show {ifname}")?
                    .Replace("GENERAL.CONNECTION:", "").Trim();

                string ethtoolOutput = RunCommand("ethtool", ifname);
                var speedMatch = ethtoolOutput != null ? Regex.Match(ethtoolOutput, @"Speed:\s*(\d+)Mb/s") : Match.Empty;

                return new GatewayLinkInfo
                {
                    ConnectionName = !string.IsNullOrWhiteSpace(connName) && connName != "--" ? connName : $"Wired ({ifname})",
                    IsWireless = false,
                    Band = null,
                    MaxCapability = speedMatch.Success ? FormatMbps(double.Parse(speedMatch.Groups[1].Value)) : null
                };
            }
        }

        // ── Windows: netsh wlan for Wi-Fi, PowerShell Get-NetAdapter for wired ──

        private static GatewayLinkInfo GetWindows(NetworkInterfaceOption option)
        {
            bool isWireless = option.Kind == NetworkInterfaceKind.WiFi;

            if (isWireless)
            {
                string output = RunCommand("netsh", "wlan show interfaces");
                if (string.IsNullOrWhiteSpace(output)) return GatewayLinkInfo.Unavailable(option.DisplayName, true);

                // Multiple adapters can be listed; take the block matching this
                // adapter's friendly name (falling back to the first block).
                string block = ExtractInterfaceBlock(output, option.DisplayName);

                var ssidMatch = Regex.Match(block, @"^\s*SSID\s*:\s*(.+)$", RegexOptions.Multiline);
                var channelMatch = Regex.Match(block, @"Channel\s*:\s*(\d+)");
                var rxMatch = Regex.Match(block, @"Receive rate \(Mbps\)\s*:\s*([\d.]+)");
                var txMatch = Regex.Match(block, @"Transmit rate \(Mbps\)\s*:\s*([\d.]+)");

                double maxRate = 0;
                if (rxMatch.Success) maxRate = Math.Max(maxRate, double.Parse(rxMatch.Groups[1].Value));
                if (txMatch.Success) maxRate = Math.Max(maxRate, double.Parse(txMatch.Groups[1].Value));

                return new GatewayLinkInfo
                {
                    ConnectionName = ssidMatch.Success ? ssidMatch.Groups[1].Value.Trim() : option.DisplayName,
                    IsWireless = true,
                    Band = channelMatch.Success ? BandFromChannel(int.Parse(channelMatch.Groups[1].Value)) : null,
                    MaxCapability = maxRate > 0 ? FormatMbps(maxRate) : null
                };
            }
            else
            {
                string speed = RunCommand("powershell", $"-NoProfile -Command \"(Get-NetAdapter -Name '{option.DisplayName}').LinkSpeed\"")?.Trim();

                return new GatewayLinkInfo
                {
                    ConnectionName = option.DisplayName,
                    IsWireless = false,
                    Band = null,
                    MaxCapability = string.IsNullOrWhiteSpace(speed) ? null : speed
                };
            }
        }

        private static string ExtractInterfaceBlock(string netshOutput, string displayName)
        {
            // Each adapter's block starts with "Name" and runs until the next
            // "Name" line or end of output.
            var blocks = Regex.Split(netshOutput, @"(?=^\s*Name\s*:)", RegexOptions.Multiline);
            foreach (var block in blocks)
            {
                if (block.Contains($"Name", StringComparison.OrdinalIgnoreCase) &&
                    block.IndexOf(displayName ?? string.Empty, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return block;
                }
            }
            return blocks.Length > 1 ? blocks[1] : netshOutput;
        }

        // ── macOS: system_profiler for Wi-Fi, ifconfig for wired ──

        private static GatewayLinkInfo GetMac(NetworkInterfaceOption option)
        {
            bool isWireless = option.Kind == NetworkInterfaceKind.WiFi;
            string ifname = option.OsInterfaceId ?? "en0";

            if (isWireless)
            {
                string output = RunCommand("system_profiler", "SPAirPortDataType");
                if (!string.IsNullOrWhiteSpace(output))
                {
                    int idx = output.IndexOf("Current Network Information:", StringComparison.OrdinalIgnoreCase);
                    string section = idx >= 0 ? output.Substring(idx) : output;

                    // First indented "Name:" line right after the header is the SSID.
                    var ssidMatch = Regex.Match(section, @"Current Network Information:\s*\r?\n\s*([^\r\n:]+):");
                    var bandMatch = Regex.Match(section, @"(2\.4|5|6)\s*GHz");
                    var rateMatch = Regex.Match(section, @"Transmit Rate:\s*(\d+)");

                    if (ssidMatch.Success)
                    {
                        return new GatewayLinkInfo
                        {
                            ConnectionName = ssidMatch.Groups[1].Value.Trim(),
                            IsWireless = true,
                            Band = bandMatch.Success ? $"{bandMatch.Groups[1].Value} GHz" : null,
                            MaxCapability = rateMatch.Success ? FormatMbps(double.Parse(rateMatch.Groups[1].Value)) : null
                        };
                    }
                }

                return GatewayLinkInfo.Unavailable(ifname, true);
            }
            else
            {
                string output = RunCommand("ifconfig", ifname);
                var speedMatch = output != null ? Regex.Match(output, @"media:.*?(\d+)base") : Match.Empty;

                return new GatewayLinkInfo
                {
                    ConnectionName = option.DisplayName,
                    IsWireless = false,
                    Band = null,
                    MaxCapability = speedMatch.Success ? FormatMbps(double.Parse(speedMatch.Groups[1].Value)) : null
                };
            }
        }

        // ── Shared helpers ──

        private static string BandFromFrequencyMHz(int freqMHz)
        {
            if (freqMHz < 2500) return "2.4 GHz";
            if (freqMHz < 5925) return "5 GHz";
            return "6 GHz";
        }

        private static string BandFromChannel(int channel)
        {
            // Rough but standard-compliant: channels 1-14 are 2.4 GHz, 36-177
            // are 5 GHz, and 6 GHz (Wi-Fi 6E) channels overlap that numbering
            // so they can't be told apart from the channel number alone --
            // frequency (used on Linux/macOS) is preferred when available.
            if (channel <= 14) return "2.4 GHz";
            return "5 GHz";
        }

        private static string FormatMbps(double mbps)
        {
            if (mbps >= 1000) return $"{mbps / 1000.0:0.##} Gbps";
            return $"{mbps:0.#} Mbps";
        }

        private static string RunCommand(string fileName, string arguments, int timeoutMs = 2000)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null) return null;

                string output = process.StandardOutput.ReadToEnd();
                if (!process.WaitForExit(timeoutMs))
                {
                    try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                    return null;
                }

                return output;
            }
            catch (Exception ex)
            {
                Logger.Write($"[GATEWAY INFO] '{fileName} {arguments}' failed: {ex.Message}");
                return null;
            }
        }
    }
}
