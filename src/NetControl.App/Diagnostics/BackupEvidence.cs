using System.Net;
using NetControl.App.Serving;
using NetControl.Core.Dhcp;
using NetControl.Core.Tftp;

namespace NetControl.App.Diagnostics;

/// <summary>
/// Everything this session has seen of one image backup attempt, gathered from both listeners.
///
/// <para>A plain record so that <see cref="BackupVerdict"/> is a pure function of it: the panel's
/// one sentence can then be tested case by case without a socket, a clock or a robot - which is the
/// only way the "which step failed" wording gets tested before it is in front of a FANUC.</para>
/// </summary>
public sealed record BackupEvidence
{
    /// <summary>Whether the BOOTP/DHCP listener is running, and so whether step 1 can be seen at all.</summary>
    public ServerRunState DhcpState { get; init; } = ServerRunState.Stopped;

    public DhcpServerMode DhcpMode { get; init; } = DhcpServerMode.Watch;

    /// <summary>Address requests seen since the evidence was last cleared, retransmits included.</summary>
    public int AddressRequests { get; init; }

    /// <summary>The device the verdict is about: its MAC, with the vendor when the OUI table knows it.</summary>
    public string? Requester { get; init; }

    /// <summary>How many times <see cref="Requester"/> has asked. A controller that keeps asking did
    /// not get, or did not accept, an answer.</summary>
    public int RequesterAttempts { get; init; }

    /// <summary>The address NetControl handed <see cref="Requester"/>, if it handed one out.</summary>
    public IPAddress? ServedAddress { get; init; }

    /// <summary>UDP/69 on this machine, graded for the vantage the user said they are at.</summary>
    public ReadinessCheck? ServerPort { get; init; }

    /// <summary>The inbound UDP/69 firewall rule.</summary>
    public ReadinessCheck? Firewall { get; init; }

    /// <summary>Whether the TFTP watch is running, and so whether step 3 can be seen at all.</summary>
    public ServerRunState WatchState { get; init; } = ServerRunState.Stopped;

    /// <summary>Why the watch stopped on its own, when it did.</summary>
    public string? WatchStopReason { get; init; }

    /// <summary>The file request the verdict is about - the latest write request, else the latest of any kind.</summary>
    public TftpRequestEventArgs? FileRequest { get; init; }

    /// <summary>File requests seen since the evidence was last cleared, retransmits included.</summary>
    public int FileRequests { get; init; }
}
