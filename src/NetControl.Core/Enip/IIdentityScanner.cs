using System.Net;
using NetControl.Core.Interfaces;

namespace NetControl.Core.Enip;

/// <summary>
/// The scan, as everything above it needs to see it.
///
/// <para>An interface for the same reason <see cref="INicInventory"/> is one: the real
/// implementation opens a socket and waits two seconds, and a test of what the tool <em>does</em>
/// with a set of replies should not have to produce those replies over a wire to ask the question.
/// <see cref="IdentityScanner"/> is the implementation, and it is tested against the simulator
/// over a real socket.</para>
/// </summary>
public interface IIdentityScanner
{
    /// <summary>
    /// Broadcasts one ListIdentity from <paramref name="nic"/>, optionally probes the addresses in
    /// <paramref name="unicastTargets"/>, and collects what answers within the window.
    ///
    /// <para><b>This transmits.</b> It is never automatic and never on a timer; the caller is a
    /// button somebody pressed.</para>
    /// </summary>
    Task<ScanReport> ScanAsync(
        NicInfo nic,
        IReadOnlyList<IPAddress>? unicastTargets = null,
        CancellationToken cancellationToken = default);
}
