namespace Arpoon
{
    /// <summary>Snapshot of the active network adapter's radio/link details for
    /// the sidebar's Gateway card. All fields are best-effort: on any platform
    /// where the underlying OS tool is missing or its output doesn't parse,
    /// the corresponding field stays null and the UI shows "—".</summary>
    public class GatewayLinkInfo
    {
        /// <summary>SSID for Wi-Fi, or a human-readable connection name for a
        /// wired link.</summary>
        public string ConnectionName { get; set; }

        public bool IsWireless { get; set; }

        /// <summary>"2.4 GHz" / "5 GHz" / "6 GHz". Null for a wired link or
        /// when the band couldn't be determined.</summary>
        public string Band { get; set; }

        /// <summary>The link's negotiated/maximum PHY rate, e.g. "866 Mbps"
        /// or "1 Gbps" -- the best available proxy for "the router's max
        /// capability" since the app has no way to query the AP directly.</summary>
        public string MaxCapability { get; set; }

        public static GatewayLinkInfo Unavailable(string fallbackName = null, bool isWireless = false) => new GatewayLinkInfo
        {
            ConnectionName = string.IsNullOrWhiteSpace(fallbackName) ? "—" : fallbackName,
            IsWireless = isWireless,
            Band = null,
            MaxCapability = null
        };
    }
}
