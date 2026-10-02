using System.Globalization;
using NetControl.Core.Cip;

namespace NetControl.Core.DeviceHealth;

/// <summary>
/// One Ethernet Link instance - one physical port - as read. Every field is optional, because each
/// attribute is read on its own and a device that refuses one still answers the rest.
/// </summary>
public sealed record LinkPort
{
    /// <summary>The Ethernet Link instance number. Port 1 is instance 1 on every device seen so far.</summary>
    public required ushort Instance { get; init; }

    /// <summary>Mb/s. 0 with no link.</summary>
    public uint? SpeedMbps { get; init; }

    public LinkFlags? Flags { get; init; }

    public MacAddress? Mac { get; init; }

    public InterfaceCounters? Interface { get; init; }

    public MediaCounters? Media { get; init; }

    /// <summary>Attributes the device would not give up, and its reason - "media counters: not supported".</summary>
    public IReadOnlyList<string> Refused { get; init; } = [];

    public bool HasLink => Flags is { LinkActive: true } || SpeedMbps is > 0;

    /// <summary>"100 Mb/s full duplex, negotiated" - or "no link".</summary>
    public string Summary
    {
        get
        {
            if (Flags is { LinkActive: false })
            {
                return "no link";
            }

            string speed = SpeedMbps is { } mbps
                ? string.Create(CultureInfo.InvariantCulture, $"{mbps} Mb/s")
                : "speed unknown";

            if (Flags is not { } flags)
            {
                return speed;
            }

            string negotiation = flags.Negotiation switch
            {
                NegotiationStatus.Negotiated => "negotiated",
                NegotiationStatus.Forced => "forced",
                NegotiationStatus.InProgress => "negotiating",
                NegotiationStatus.FailedUsingDefaults => "negotiation failed",
                NegotiationStatus.DuplexDefaulted => "duplex not negotiated",
                _ => "negotiation status reserved",
            };

            return $"{speed} {(flags.FullDuplex ? "full" : "half")} duplex, {negotiation}";
        }
    }
}
