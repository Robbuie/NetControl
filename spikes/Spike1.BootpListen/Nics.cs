using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NetControl.Spike.BootpListen;

public sealed record NicInfo(
    int Index,
    string Name,
    string Description,
    IPAddress? IPv4,
    IPAddress? Mask,
    OperationalStatus Status,
    NetworkInterfaceType Type,
    long SpeedBps)
{
    public bool IsApipa => IPv4 is not null && IPv4.GetAddressBytes() is [169, 254, ..];

    public override string ToString()
    {
        // Build the address as one string before padding it - padding the mask alone
        // leaves the columns ragged, because the IP itself varies in width.
        string addr = IPv4 is null
            ? "no IPv4"
            : $"{IPv4}/{(Mask is null ? "?" : MaskToPrefix(Mask).ToString())}";

        string speed = SpeedBps > 0 ? $"{SpeedBps / 1_000_000} Mb/s" : "-";
        string warn = IsApipa ? "  [APIPA]" : "";
        return $"[{Index,3}] {Name,-30} {addr,-19} {Status,-6} {speed,-10} {Description}{warn}";
    }

    public static int MaskToPrefix(IPAddress mask)
    {
        int bits = 0;
        foreach (byte b in mask.GetAddressBytes()) bits += System.Numerics.BitOperations.PopCount((uint)b);
        return bits;
    }
}

public static class Nics
{
    /// <summary>
    /// Every IPv4-capable interface, keyed by the interface index the IP stack reports -
    /// the same index that comes back in IPPacketInformation.Interface.
    /// </summary>
    public static List<NicInfo> All()
    {
        var list = new List<NicInfo>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!ni.Supports(NetworkInterfaceComponent.IPv4)) continue;

            // A device can never send us a BOOTP request over loopback, and on a machine
            // full of virtual adapters every removable row helps.
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            IPv4InterfaceProperties? v4;
            try { v4 = ni.GetIPProperties().GetIPv4Properties(); }
            catch { continue; }
            if (v4 is null) continue;

            var uni = ni.GetIPProperties().UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);

            long speed;
            try { speed = ni.Speed; } catch { speed = -1; }

            list.Add(new NicInfo(v4.Index, ni.Name, ni.Description,
                                 uni?.Address, uni?.IPv4Mask,
                                 ni.OperationalStatus, ni.NetworkInterfaceType, speed));
        }
        return list.OrderBy(n => n.Index).ToList();
    }

    public static NicInfo? ByIndex(int index) => All().FirstOrDefault(n => n.Index == index);

    /// <summary>Matches on interface name or a substring of the adapter description.</summary>
    public static NicInfo? Find(string nameOrDescription)
    {
        var all = All();
        return all.FirstOrDefault(n => n.Name.Equals(nameOrDescription, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(n => n.Description.Contains(nameOrDescription, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(n => n.Name.Contains(nameOrDescription, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Index-to-NIC lookup with a short TTL. Enumerating adapters costs milliseconds,
/// which is far too expensive to do on every received packet, but adapters do come
/// and go (USB dongles, VPN up/down) so the cache cannot be permanent either.
/// </summary>
public sealed class NicCache(TimeSpan? ttl = null)
{
    private readonly TimeSpan _ttl = ttl ?? TimeSpan.FromSeconds(5);
    private Dictionary<int, NicInfo> _byIndex = new();
    private List<NicInfo> _all = [];
    private DateTime _stamp = DateTime.MinValue;

    private void RefreshIfStale()
    {
        if (DateTime.UtcNow - _stamp < _ttl) return;
        _all = Nics.All();
        // Group rather than ToDictionary: a duplicate index would otherwise throw,
        // and a startup crash is a worse outcome than preferring the first match.
        _byIndex = _all.GroupBy(n => n.Index).ToDictionary(g => g.Key, g => g.First());
        _stamp = DateTime.UtcNow;
    }

    public NicInfo? ByIndex(int index)
    {
        RefreshIfStale();
        return _byIndex.GetValueOrDefault(index);
    }

    public IReadOnlyList<NicInfo> Snapshot()
    {
        RefreshIfStale();
        return _all;
    }
}
