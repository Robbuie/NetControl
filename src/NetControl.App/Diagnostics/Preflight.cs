using NetControl.Core.Interfaces;

namespace NetControl.App.Diagnostics;

/// <summary>
/// The real environment check, run off the UI thread.
///
/// The thread hop is not incidental. <see cref="FirewallCheck.Inspect"/> enumerates every rule in
/// the Windows Firewall policy store over late-bound COM; on a domain-joined machine with a few
/// hundred group-policy rules that is comfortably long enough to freeze a window, and a tool
/// whose whole pitch is "it tells you why nothing is happening" cannot be the thing that hangs.
/// </summary>
public sealed class Preflight(TimeProvider? timeProvider = null) : IPreflight
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public Task<PreflightResult> InspectAsync(int port, CancellationToken cancellationToken = default) =>
        Task.Run(
            () =>
            {
                PortConflictReport conflict = PortConflictDetector.Inspect(port);
                cancellationToken.ThrowIfCancellationRequested();

                FirewallStatus firewall = FirewallCheck.Inspect(port);
                return new PreflightResult(conflict, firewall, _time.GetUtcNow());
            },
            cancellationToken);
}
