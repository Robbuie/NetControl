using System.Net;
using System.Net.NetworkInformation;
using System.Numerics;

namespace NetControl.Core.Interfaces;

/// <summary>
/// Immutable snapshot of one IPv4-capable adapter.
///
/// A snapshot, deliberately: <see cref="NetworkInterface"/> objects are live handles that are
/// expensive to query and awkward to hold across threads. Everything the engine and the UI need
/// is copied out once by <see cref="NicMonitor"/> and then treated as a value.
/// </summary>
/// <param name="Index">
/// The IPv4 interface index. This is the same number that comes back in
/// <see cref="System.Net.Sockets.IPPacketInformation.Interface"/>, which is what makes per-packet attribution work.
/// </param>
public sealed record NicInfo(
    int Index,
    string Name,
    string Description,
    MacAddress Mac,
    IPAddress? IPv4,
    IPAddress? Mask,
    OperationalStatus Status,
    NetworkInterfaceType Type,
    long SpeedBps)
{
    /// <summary>169.254.x.x - the adapter asked for a lease and never got one.</summary>
    public bool IsApipa => IPv4 is not null && IPv4.GetAddressBytes() is [169, 254, ..];

    public bool IsUp => Status == OperationalStatus.Up;

    /// <summary>
    /// True when we could actually source a reply from this adapter. A BOOTP reply is a limited
    /// broadcast, but the socket still needs a real address on the segment to send it from, so
    /// an adapter with no address or an APIPA address cannot serve.
    /// </summary>
    public bool CanServe => IsUp && IPv4 is not null && !IsApipa;

    public int? PrefixLength => Mask is null ? null : MaskToPrefixLength(Mask);

    /// <summary>
    /// Best-effort "this is not a physical port" flag, used only to sort and dim the list in the
    /// UI. An engineering laptop routinely shows a dozen adapters of which one is real, and
    /// picking the right one by guesswork is the problem the tool exists to solve. It is a
    /// heuristic on the driver description, so it never drives a decision - only presentation.
    /// </summary>
    public bool IsLikelyVirtual =>
        Type is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Loopback ||
        ContainsAny(Description, VirtualAdapterMarkers) ||
        ContainsAny(Name, VirtualAdapterMarkers);

    /// <summary>
    /// Substrings that mark an adapter as not a physical port, matched against both the name and
    /// the driver description.
    ///
    /// <para>The bottom three came off a real engineering laptop that carried fourteen adapters,
    /// of which one was the Ethernet port anybody wanted. Every entry here should be traceable to
    /// a machine somebody actually met - guessing at vendor strings produces a list that is long,
    /// stale, and no better at sorting.</para>
    /// </summary>
    private static readonly string[] VirtualAdapterMarkers =
    [
        "VMware", "VirtualBox", "Hyper-V", "vEthernet", "TAP-", "TAP Windows", "OpenVPN",
        "Loopback", "Bluetooth", "Wi-Fi Direct", "WAN Miniport", "Npcap", "WireGuard",
        "Tailscale", "ZeroTier", "Docker", "WSL",

        // "Microsoft Network Adapter Multiplexor Driver" - a Network Bridge or an LBFO team.
        // Real ports underneath, but never the thing to commission from.
        "Multiplexor",

        // "Cisco AnyConnect Virtual Miniport Adapter for Windows x64", and the same shape from
        // most other corporate VPN clients.
        "Virtual Miniport",

        // "Wintun Userspace Tunnel" - WireGuard's and OpenVPN's newer driver. The name usually
        // carries the client, but the description alone does not.
        "Wintun",
    ];

    private static bool ContainsAny(string haystack, string[] needles)
    {
        foreach (string needle in needles)
        {
            if (haystack.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Counts set bits rather than assuming the mask is contiguous. A non-contiguous mask is
    /// invalid but does turn up in the wild on misconfigured equipment, and a wrong prefix in a
    /// display is better than an exception in the interface bar.
    /// </summary>
    public static int MaskToPrefixLength(IPAddress mask)
    {
        ArgumentNullException.ThrowIfNull(mask);
        int bits = 0;
        foreach (byte b in mask.GetAddressBytes())
        {
            bits += BitOperations.PopCount(b);
        }

        return bits;
    }
}
