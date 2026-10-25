using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;

namespace Arpoon
{
    /// <summary>Callback when a device is detected or removed.</summary>
    public delegate void OnDeviceEvent(PC pc);

    public class PcList : IDisposable
    {
        private OnDeviceEvent _onDeviceAdded;
        private OnDeviceEvent _onDeviceRemoved;
        private IDeviceIdentifierService _identifierService;

        private readonly object _syncLock = new();
        private readonly List<PC> _devices = new();

        // O(1) indices mirroring _devices, kept in sync under _syncLock.
        // AddDevice/GetDeviceByIp run on the ARP listener's and mDNS listener's
        // hot per-packet paths, where a List<PC> linear scan would otherwise
        // cost O(n) per packet against potentially hundreds of devices.
        private readonly Dictionary<IPAddress, PC> _byIp = new();
        private readonly Dictionary<string, PC> _byMac = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Returns a thread-safe snapshot (array copy) for external iteration.
        /// </summary>
        public IReadOnlyList<PC> Devices
        {
            get
            {
                lock (_syncLock)
                {
                    return _devices.ToArray();
                }
            }
        }

        public bool AddDevice(PC pc)
        {
            if (pc?.Ip == null) return false;

            bool isNew = false;
            lock (_syncLock)
            {
                if (_byIp.TryGetValue(pc.Ip, out PC item))
                {
                    item.TimeSinceLastArp = DateTime.Now;
                    // Update MAC if changed or newly detected
                    if (pc.Mac != null && (item.Mac == null || !item.Mac.Equals(pc.Mac)))
                    {
                        if (item.Mac != null) _byMac.Remove(item.Mac.ToString());
                        item.Mac = pc.Mac;
                        _byMac[pc.Mac.ToString()] = item;
                    }
                    if (pc.IsGateway && !item.IsGateway) item.IsGateway = true;
                    if (pc.IsLocalPc && !item.IsLocalPc) item.IsLocalPc = true;
                    return false;
                }

                _devices.Add(pc);
                _byIp[pc.Ip] = pc;
                if (pc.Mac != null) _byMac[pc.Mac.ToString()] = pc;
                isNew = true;
            }

            if (isNew)
            {
                // Invoke callback outside lock to prevent UI thread deadlocks
                _onDeviceAdded?.Invoke(pc);

                // Fire-and-forget device identification (OUI + DNS + heuristic)
                if (_identifierService != null)
                {
                    _ = _identifierService.IdentifyDeviceAsync(pc, CancellationToken.None);
                }
            }

            return true;
        }

        public bool RemoveDevice(PC pc)
        {
            if (pc?.Ip == null) return false;
            PC found = null;
            lock (_syncLock)
            {
                if (_byIp.TryGetValue(pc.Ip, out PC item))
                {
                    found = item;
                    _devices.Remove(item);
                    _byIp.Remove(pc.Ip);
                    if (item.Mac != null) _byMac.Remove(item.Mac.ToString());
                }
            }

            if (found != null)
            {
                _onDeviceRemoved?.Invoke(found);
                return true;
            }
            return false;
        }

        public PC GetRouter()
        {
            lock (_syncLock)
            {
                foreach (PC item in _devices)
                {
                    if (item.IsGateway) return item;
                }
            }
            return null;
        }

        public PC GetLocalPC()
        {
            lock (_syncLock)
            {
                foreach (PC item in _devices)
                {
                    if (item.IsLocalPc) return item;
                }
            }
            return null;
        }

        public PC GetDeviceByIp(IPAddress ip)
        {
            if (ip == null) return null;
            lock (_syncLock)
            {
                return _byIp.TryGetValue(ip, out PC item) ? item : null;
            }
        }

        public PC GetDeviceByIp(byte[] ip)
        {
            if (ip == null || ip.Length != 4) return null;
            return GetDeviceByIp(new IPAddress(ip));
        }

        public PC GetDeviceByMac(byte[] mac)
        {
            if (mac == null) return null;
            string key = new PhysicalAddress(mac).ToString();
            lock (_syncLock)
            {
                return _byMac.TryGetValue(key, out PC item) ? item : null;
            }
        }

        public void SetOnDeviceAdded(OnDeviceEvent callback)
        {
            _onDeviceAdded += callback;
        }

        public void SetOnDeviceRemoved(OnDeviceEvent callback)
        {
            _onDeviceRemoved += callback;
        }

        public void SetIdentifierService(IDeviceIdentifierService service)
        {
            _identifierService = service;
        }

        public void Clear()
        {
            lock (_syncLock)
            {
                _devices.Clear();
                _byIp.Clear();
                _byMac.Clear();
            }
        }

        public void Dispose()
        {
            Clear();
            GC.SuppressFinalize(this);
        }
    }
}
