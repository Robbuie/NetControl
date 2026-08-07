namespace NetControl.Core.Interfaces;

/// <summary>
/// What we found out about who owns a port, phrased so the UI can show it verbatim.
/// Core builds the sentence because Core is the only layer that knows what the finding means.
/// </summary>
public sealed record PortConflictReport(
    int Port,
    PortConflictSeverity Severity,
    IReadOnlyList<UdpPortOwner> Owners,
    string Summary,
    string? Remediation)
{
    public bool IsClear => Severity == PortConflictSeverity.None;

    /// <summary>Used when the platform cannot be queried, so the UI can say "unknown" honestly.</summary>
    public static PortConflictReport Unknown(int port) => new(
        port,
        PortConflictSeverity.None,
        [],
        $"Could not determine what owns UDP/{port} on this platform.",
        null);
}
