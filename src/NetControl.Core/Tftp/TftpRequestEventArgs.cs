using System.Net;
using NetControl.Core.Interfaces;

namespace NetControl.Core.Tftp;

/// <summary>
/// One TFTP request as it landed: what was asked for, from where, on which adapter, and what this
/// tool did about it.
///
/// <para>The source port is carried and is not incidental. It is the client's transfer identifier,
/// it is what a reply has to be addressed to, and a second request from a different port is a
/// second attempt rather than a retransmit of the first.</para>
/// </summary>
public sealed class TftpRequestEventArgs(
    DateTimeOffset timestamp,
    TftpRequestMessage request,
    IPEndPoint source,
    int arrivalInterfaceIndex,
    NicInfo? arrivalNic,
    IPAddress arrivalAddress,
    bool isRetransmit,
    TftpWatchAction action,
    string reason) : EventArgs()
{
    public DateTimeOffset Timestamp { get; } = timestamp;

    public TftpRequestMessage Request { get; } = request;

    /// <summary>Who asked, and from which port - their transfer identifier.</summary>
    public IPEndPoint Source { get; } = source;

    public int ArrivalInterfaceIndex { get; } = arrivalInterfaceIndex;

    /// <summary>The adapter it arrived on, or null when the adapter is unknown to the inventory.</summary>
    public NicInfo? ArrivalNic { get; } = arrivalNic;

    /// <summary>The local address the datagram was delivered to.</summary>
    public IPAddress ArrivalAddress { get; } = arrivalAddress;

    /// <summary>True when an identical request from the same source arrived inside the window.</summary>
    public bool IsRetransmit { get; } = isRetransmit;

    public TftpWatchAction Action { get; } = action;

    /// <summary>Why that action, in words the log can show verbatim.</summary>
    public string Reason { get; } = reason;

    /// <summary>
    /// The local port a refusal was sent from, or null when nothing was sent.
    ///
    /// <para>Carried because it is evidence rather than trivia. Whether a controller accepts a
    /// refusal that arrives from the well-known port, or only one that carries a transfer
    /// identifier of its own, is an open question - and the row that records the refusal has to say
    /// which of the two it was, or the observation cannot be interpreted afterwards.</para>
    /// </summary>
    public int? RefusalSourcePort { get; init; }

    /// <summary>
    /// One line for the log: who asked, for what, and on which adapter. The adapter is in here
    /// because a request arriving somewhere nobody is looking is the answer to "why is nothing
    /// happening", and it is the same reasoning that put it in the DHCP request log.
    /// </summary>
    public string Describe()
    {
        string where = ArrivalNic is { } nic
            ? $"[{nic.Index}] {nic.Name}"
            : $"interface {ArrivalInterfaceIndex}";

        return $"{Source} on {where}: {Request.Describe()}";
    }
}
