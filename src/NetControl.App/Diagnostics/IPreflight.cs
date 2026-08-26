namespace NetControl.App.Diagnostics;

/// <summary>
/// Measures the two things about the machine that stop a request arriving: a port conflict and
/// the firewall.
///
/// An interface because the real one reads the firewall over COM, which on a locked-down laptop
/// is slow and occasionally refuses outright. A view model test wants to assert how a
/// <c>Blocked</c> verdict is presented, not to discover what this machine's firewall says today.
/// </summary>
public interface IPreflight
{
    /// <summary>
    /// Inspects the port and the firewall. Never throws for an environment it cannot read - the
    /// unreadable answer is <c>Unknown</c>, which the bar shows as grey rather than green.
    /// </summary>
    Task<PreflightResult> InspectAsync(int port, CancellationToken cancellationToken = default);
}
