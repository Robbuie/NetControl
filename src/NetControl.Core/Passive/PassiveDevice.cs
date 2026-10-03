using System.Net;

namespace NetControl.Core.Passive;

/// <summary>Everything heard from one MAC address so far.</summary>
public sealed record PassiveDevice
{
    /// <summary>More addresses than this behind one MAC is a router or a gateway forwarding for others.</summary>
    public const int RouterThreshold = 4;

    public required MacAddress Mac { get; init; }

    public string? Vendor { get; init; }

    /// <summary>Every IPv4 address seen with this MAC, ARP and DHCP first, then traffic.</summary>
    public IReadOnlyList<IPAddress> Addresses { get; init; } = [];

    /// <summary>Every name it gave itself, most recent first.</summary>
    public IReadOnlyList<string> Names { get; init; } = [];

    public PassiveSource Sources { get; init; }

    /// <summary>The most telling detail line heard - a conflict probe, a DHCP vendor class, an LLDP port.</summary>
    public string? Detail { get; init; }

    public ushort? Vlan { get; init; }

    public required DateTimeOffset FirstHeardUtc { get; init; }

    public required DateTimeOffset LastHeardUtc { get; init; }

    public long Frames { get; init; }

    /// <summary>Many addresses behind one MAC: a router, a gateway, or this PC's own default route.</summary>
    public bool LooksLikeRouter => Addresses.Count >= RouterThreshold;

    /// <summary>Asked for an address and was never seen holding one - a device waiting for BOOTP.</summary>
    public bool IsWaitingForAddress => Sources.HasFlag(PassiveSource.Dhcp) && Addresses.Count == 0;

    public string AddressText => Addresses.Count switch
    {
        0 => string.Empty,
        <= 2 => string.Join(", ", Addresses),
        _ => $"{Addresses[0]} and {Addresses.Count - 1} more",
    };

    public string NameText => Names.Count == 0 ? string.Empty : Names[0];

    public string SourcesText => string.Join(", ", Enum.GetValues<PassiveSource>()
        .Where(s => s != PassiveSource.None && Sources.HasFlag(s))
        .Select(Label));

    public static string Label(PassiveSource source) => source switch
    {
        PassiveSource.Arp => "ARP",
        PassiveSource.Dhcp => "BOOTP/DHCP",
        PassiveSource.Lldp => "LLDP",
        PassiveSource.ProfinetDcp => "PROFINET DCP",
        PassiveSource.ProfinetIo => "PROFINET I/O",
        PassiveSource.EnipIdentity => "EtherNet/IP identity",
        PassiveSource.EnipIo => "EtherNet/IP I/O",
        PassiveSource.IPv4 => "IP",
        _ => source.ToString(),
    };
}
