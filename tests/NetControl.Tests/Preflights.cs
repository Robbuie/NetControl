using NetControl.App.Diagnostics;
using NetControl.Core.Interfaces;

namespace NetControl.Tests;

/// <summary>
/// Port and firewall findings the test states outright, rather than whatever this machine says today.
/// </summary>
internal static class Preflights
{
    public static PreflightResult Clear() => new(
        Port(PortConflictSeverity.None, "UDP/67 is free."),
        Firewall(FirewallVerdict.Allowed, "Inbound UDP/67 is allowed by NetControl inbound UDP/67."),
        Events.At);

    public static PortConflictReport Port(
        PortConflictSeverity severity,
        string summary,
        string? remediation = null,
        int port = 67) =>
        new(port, severity, [], summary, remediation);

    public static FirewallStatus Firewall(
        FirewallVerdict verdict,
        string summary,
        string? remediation = null,
        int port = 67) =>
        new(verdict, port, [], [], summary, remediation);
}

/// <summary>Answers instantly with a clear environment, so no test reads this machine's firewall.</summary>
internal sealed class StubPreflight : IPreflight
{
    public Task<PreflightResult> InspectAsync(int port, CancellationToken cancellationToken = default) =>
        Task.FromResult(Preflights.Clear());
}
