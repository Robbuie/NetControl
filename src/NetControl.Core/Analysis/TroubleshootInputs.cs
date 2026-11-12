using NetControl.Core.Interfaces;
using NetControl.Core.Reachability;

namespace NetControl.Core.Analysis;

/// <summary>
/// Everything the troubleshooter judges, gathered first so the judging is a pure function a test
/// can drive with any combination of facts.
/// </summary>
public sealed record TroubleshootInputs
{
    /// <summary>The adapter selected at the top of the window.</summary>
    public NicInfo? Adapter { get; init; }

    public IReadOnlyList<NicInfo> AllAdapters { get; init; } = [];

    /// <summary>Who holds UDP/67, when it was read.</summary>
    public PortConflictReport? DhcpPort { get; init; }

    public FirewallStatus? Firewall { get; init; }

    /// <summary>This PC's adapter counters, read twice a few seconds apart.</summary>
    public NicCounters? CountersBefore { get; init; }

    public NicCounters? CountersAfter { get; init; }

    /// <summary>True when Windows itself has flagged this PC's address as a duplicate.</summary>
    public bool? OwnAddressDuplicate { get; init; }

    public IReadOnlyList<PlannedAddress> Plan { get; init; } = [];

    public IReadOnlyList<ArpEntry> Arp { get; init; } = [];

    public IReadOnlyList<AddressConflict> Conflicts { get; init; } = [];

    /// <summary>The traffic monitor's view, when the Passive tab is listening.</summary>
    public TrafficSnapshot? Traffic { get; init; }

    /// <summary>Distinct MACs that asked for an address recently and are not in the plan.</summary>
    public int UnplannedAskers { get; init; }

    public bool ListenOnly { get; init; }

    /// <summary>True when the ping checks were run. False when skipped - by choice or by listen-only.</summary>
    public bool ActiveChecksRan { get; init; }

    public PingOutcome? GatewayPing { get; init; }

    public IReadOnlyList<(PlannedAddress Device, PingOutcome Outcome)> PlanPings { get; init; } = [];
}
