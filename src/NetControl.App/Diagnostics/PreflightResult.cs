using NetControl.Core.Interfaces;

namespace NetControl.App.Diagnostics;

/// <summary>
/// What the two environment checks found. Kept together because they are always shown together
/// and always measured together, and because a bar that refreshes them independently flickers.
/// </summary>
/// <param name="Port">Who else holds UDP/67, graded.</param>
/// <param name="Firewall">Whether an inbound datagram would reach this executable.</param>
/// <param name="CheckedUtc">When this was measured. Both answers go stale the moment a service starts.</param>
public sealed record PreflightResult(
    PortConflictReport Port,
    FirewallStatus Firewall,
    DateTimeOffset CheckedUtc);
