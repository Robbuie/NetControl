using NetControl.Core.Interfaces;

namespace NetControl.Core.Analysis;

/// <summary>What the caller already knows when it asks for a troubleshoot run.</summary>
public sealed record TroubleshootRequest
{
    public NicInfo? Adapter { get; init; }

    public IReadOnlyList<NicInfo> AllAdapters { get; init; } = [];

    public IReadOnlyList<PlannedAddress> Plan { get; init; } = [];

    public IReadOnlyList<AddressConflict> Conflicts { get; init; } = [];

    public TrafficSnapshot? Traffic { get; init; }

    public int UnplannedAskers { get; init; }

    /// <summary>Ping the gateway and the planned devices on this PC's subnets. Skipped in listen-only mode.</summary>
    public bool IncludeActiveChecks { get; init; }

    /// <summary>How long to watch this PC's adapter counters. Long enough to catch a link that is erroring steadily.</summary>
    public TimeSpan CounterWindow { get; init; } = TimeSpan.FromSeconds(3);
}
