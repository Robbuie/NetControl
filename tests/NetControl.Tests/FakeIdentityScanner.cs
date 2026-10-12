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

    /// <summary>Every adapter, or adapter address, a broadcast scan went out from, in order.</summary>
    public List<NicInfo> ScannedFromAll { get; } = [];

    /// <summary>
    /// When set, a broadcast is answered only by the stated replies on the scanning address's own
    /// subnet - the way real devices answer, and what an adapter with two addresses depends on.
    /// Off by default so the tests that arrange a reply from anywhere keep working.
    /// </summary>
    public bool AnswerOnlyOwnSubnet { get; init; }

    public Task<ScanReport> ScanAsync(
        NicInfo nic,
        IReadOnlyList<IPAddress>? unicastTargets = null,
        CancellationToken cancellationToken = default)
    {
        ScannedFrom = nic;
        ScannedFromAll.Add(nic);
        Scans++;

        ListIdentityReply[] answering = AnswerOnlyOwnSubnet
            && NetControl.Core.Ipv4Subnet.TryCreate(nic.IPv4, nic.Mask, out NetControl.Core.Ipv4Subnet subnet)
            ? [.. replies.Where(r => subnet.Contains(r.SenderAddress))]
            : replies;

        // One probe, whatever replies the test stated, and however many hosts it wanted to have
        // refused. Two seconds is what a real window costs; nothing here waits for it.
        return Task.FromResult(new ScanReport(
            nic,
            answering,
            ProbesSent: 1,
            Duplicates: 0,
            Ignored: 0,
            Refused: Refused,
            Duration: TimeSpan.FromSeconds(2)));
    }

    /// <summary>Every address a unicast-only probe was sent to, across calls.</summary>
    public List<IPAddress> Probed { get; } = [];

    /// <summary>
    /// A unicast probe answers only with the stated replies whose address was asked about - the
    /// way a real device behind a router answers only when asked by name.
    /// </summary>
    public Task<ScanReport> ProbeAsync(
        NicInfo nic,
        IReadOnlyList<IPAddress> targets,
        CancellationToken cancellationToken = default)
    {
        Probed.AddRange(targets);

        ListIdentityReply[] answered = [.. replies.Where(r => targets.Contains(r.SenderAddress))];

        return Task.FromResult(new ScanReport(
            nic,
            answered,
            ProbesSent: targets.Count,
            Duplicates: 0,
            Ignored: 0,
            Refused: 0,
            Duration: TimeSpan.FromSeconds(2)));
    }
}
