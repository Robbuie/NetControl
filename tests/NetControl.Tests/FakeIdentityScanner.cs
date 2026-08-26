using System.Net;
using NetControl.Core.Enip;
using NetControl.Core.Interfaces;

namespace NetControl.Tests;

/// <summary>
/// A scan whose replies the test states outright, so nothing opens a socket.
///
/// <para>What a scan does on the wire is <see cref="IdentityScannerTests"/>'s job, against the
/// simulator. This is for the questions above it: what the tool concludes, what it records, and
/// what it refuses.</para>
/// </summary>
internal sealed class FakeIdentityScanner(params ListIdentityReply[] replies) : IIdentityScanner
{
    /// <summary>The adapter the last scan was asked to go out of, so a test can check it went out of one.</summary>
    public NicInfo? ScannedFrom { get; private set; }

    public int Scans { get; private set; }

    public int Refused { get; init; }

    public Task<ScanReport> ScanAsync(
        NicInfo nic,
        IReadOnlyList<IPAddress>? unicastTargets = null,
        CancellationToken cancellationToken = default)
    {
        ScannedFrom = nic;
        Scans++;

        // One probe, whatever replies the test stated, and however many hosts it wanted to have
        // refused. Two seconds is what a real window costs; nothing here waits for it.
        return Task.FromResult(new ScanReport(
            nic,
            replies,
            ProbesSent: 1,
            Duplicates: 0,
            Ignored: 0,
            Refused: Refused,
            Duration: TimeSpan.FromSeconds(2)));
    }
}
