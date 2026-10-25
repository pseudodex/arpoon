using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Arpoon
{
    /// <summary>
    /// Persistent set of whitelisted MAC addresses, cached as JSON under the
    /// user's application-data folder so it survives restarts and IP changes.
    /// </summary>
    public static class WhitelistStore
    {
        private static readonly object Sync = new();
        private static readonly HashSet<string> Macs = new(StringComparer.OrdinalIgnoreCase);
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Arpoon", "whitelist.json");

        static WhitelistStore()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath));
                    if (list != null) foreach (string m in list) Macs.Add(m);
                }
            }
            catch { /* corrupt/unreadable cache: start empty */ }
        }

        public static bool Contains(string mac)
        {
            if (string.IsNullOrEmpty(mac)) return false;
            lock (Sync) return Macs.Contains(mac);
        }

        public static void Set(string mac, bool whitelisted)
        {
            if (string.IsNullOrEmpty(mac)) return;
            lock (Sync)
            {
                bool changed = whitelisted ? Macs.Add(mac) : Macs.Remove(mac);
                if (!changed) return;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                    File.WriteAllText(FilePath, JsonSerializer.Serialize(Macs.OrderBy(m => m).ToList()));
                }
                catch { /* best effort */ }
            }
        }
    }
}
