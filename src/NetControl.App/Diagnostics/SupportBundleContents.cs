using NetControl.Core.Analysis;
using NetControl.Core.Diagnostics;
using NetControl.Core.Interfaces;
using NetControl.Core.Persistence;

namespace NetControl.App.Diagnostics;

/// <summary>What goes into a support bundle, gathered by the caller so the writer touches no live state.</summary>
public sealed record SupportBundleContents
{
    public required string BuildText { get; init; }

    public string ProjectName { get; init; } = "Untitled";

    public IReadOnlyList<NicInfo> Adapters { get; init; } = [];

    public NicInfo? SelectedAdapter { get; init; }

    /// <summary>One line each: UDP/67, the firewall, anything else the interface bar graded.</summary>
    public IReadOnlyList<string> EnvironmentChecks { get; init; } = [];

    public string ListenerState { get; init; } = "stopped";

    public bool ListenOnly { get; init; }

    public int MaxPacketsPerSecond { get; init; }

    public bool VerboseLog { get; init; }

    public IReadOnlyList<NetworkFinding>? Findings { get; init; }

    public string LedgerCsv { get; init; } = string.Empty;

    public IReadOnlyList<TraceLine> TraceLines { get; init; } = [];

    public IReadOnlyList<EventRecord>? ProjectEvents { get; init; }

    public string? LogsFolder { get; init; }

    public string? DataFolder { get; init; }
}
