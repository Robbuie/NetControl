namespace NetControl.Core.Analysis;

/// <summary>What the traffic monitor has seen, at one moment. Immutable; safe to bind.</summary>
public sealed record TrafficSnapshot
{
    public static TrafficSnapshot Empty { get; } = new();

    /// <summary>Seconds since listening started.</summary>
    public double Seconds { get; init; }

    public long Frames { get; init; }

    public long Bytes { get; init; }

    public long Broadcast { get; init; }

    public long Multicast { get; init; }

    public long Arp { get; init; }

    public long BootpRequests { get; init; }

    public long BootpReplies { get; init; }

    public long Stp { get; init; }

    public long Lldp { get; init; }

    /// <summary>Broadcasts a second, averaged over the last ten seconds.</summary>
    public double BroadcastPerSecond { get; init; }

    /// <summary>Multicasts a second, averaged over the last ten seconds.</summary>
    public double MulticastPerSecond { get; init; }

    public double ArpPerSecond { get; init; }

    public double FramesPerSecond { get; init; }

    /// <summary>The highest one-second broadcast count seen.</summary>
    public long PeakBroadcastPerSecond { get; init; }

    /// <summary>Broadcast plus multicast per second for the last two minutes, oldest first - for a sparkline.</summary>
    public IReadOnlyList<long> History { get; init; } = [];

    public IReadOnlyList<TrafficTalker> TopTalkers { get; init; } = [];

    public IReadOnlyList<DhcpServerSighting> OtherDhcpServers { get; init; } = [];

    /// <summary>Spanning-tree topology changes - separate events, not the BPDUs that carry each one.</summary>
    public int TopologyChanges { get; init; }

    public DateTimeOffset? LastTopologyChangeUtc { get; init; }

    /// <summary>The root bridge the last configuration BPDU named, as priority/MAC.</summary>
    public string? RootBridge { get; init; }

    public int RootBridgeChanges { get; init; }

    /// <summary>Sources not tracked individually because the table was full.</summary>
    public long UntrackedSources { get; init; }

    /// <summary>What the numbers mean - worst first. Empty until a few seconds have been heard.</summary>
    public IReadOnlyList<NetworkFinding> Findings { get; init; } = [];
}
