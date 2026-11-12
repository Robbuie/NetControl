namespace NetControl.Core.Analysis;

/// <summary>
/// Where the traffic monitor starts to say something. Defaults are deliberately well above what a
/// healthy controls segment produces, so a finding means something: a /24 of PLCs, drives and I/O
/// is typically tens of broadcasts a second, not hundreds.
/// </summary>
public sealed record TrafficThresholds
{
    /// <summary>Average broadcasts a second, over ten seconds, that earn a warning.</summary>
    public double BroadcastWarnPerSecond { get; init; } = 300;

    /// <summary>Average broadcasts a second, over ten seconds, that is called a storm.</summary>
    public double BroadcastStormPerSecond { get; init; } = 1500;

    /// <summary>ARP frames a second that earn a warning - a scan, or a device ARPing in a loop.</summary>
    public double ArpWarnPerSecond { get; init; } = 100;

    /// <summary>
    /// A single source sending more than this share of the broadcast and multicast traffic, and at
    /// least <see cref="TalkerMinimumPerSecond"/>, is named.
    /// </summary>
    public double TalkerShare { get; init; } = 0.5;

    public double TalkerMinimumPerSecond { get; init; } = 100;

    /// <summary>BOOTP/DHCP requests a second from one MAC that mark a device in a request loop.</summary>
    public double BootpFloodPerSecond { get; init; } = 5;

    /// <summary>Quiet time after which a spanning-tree topology change counts as a new event.</summary>
    public TimeSpan TopologyChangeQuiet { get; init; } = TimeSpan.FromSeconds(45);
}
