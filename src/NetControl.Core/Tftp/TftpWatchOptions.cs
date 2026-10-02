namespace NetControl.Core.Tftp;

/// <summary>How the watch server behaves, separated from what it observes.</summary>
public sealed record TftpWatchOptions
{
    /// <summary>The well-known TFTP port. Changed only to test without taking the real one.</summary>
    public int ListenPort { get; init; } = TftpLimits.ServerPort;

    public TftpWatchMode Mode { get; init; } = TftpWatchMode.Observe;

    /// <summary>
    /// Only act on requests that arrived on this interface index. Null means every adapter.
    ///
    /// <para>Requests from other adapters are still reported, exactly as the DHCP side reports
    /// them - "your controller is asking, but on an adapter you did not pick" is one of the most
    /// useful sentences this tool produces. They are never answered, though: a refusal is a
    /// transmission, and transmitting onto a segment nobody selected is not this tool's habit.</para>
    /// </summary>
    public int? InterfaceIndexFilter { get; init; }

    /// <summary>
    /// Answer an observed request with a TFTP error rather than saying nothing.
    ///
    /// <para>On by default, and it should stay on. TFTP has no other way to say no: a client that
    /// gets silence assumes the packet was lost and retransmits for as long as its timeout allows,
    /// so a silent watch turns one request into a minute of them and leaves whoever is at the
    /// pendant watching a progress bar that will never move. Turn it off only for a deliberately
    /// passive observation where nothing at all may leave this machine.</para>
    /// </summary>
    public bool SendRefusal { get; init; } = true;

    /// <summary>
    /// The code sent with a refusal. Zero - "see the message" - because the message is the part
    /// that helps, and because a code that names a specific cause would be inventing one: nothing
    /// is wrong with the request, we are simply not the backup server.
    /// </summary>
    public TftpErrorCode RefusalCode { get; init; } = TftpErrorCode.NotDefined;

    /// <summary>
    /// The message sent with a refusal. It reaches whoever is standing at the teach pendant, so
    /// it says what happened and what to do rather than naming this tool and stopping.
    /// </summary>
    public string RefusalMessage { get; init; } =
        "NetControl is watching this port, not serving it. The request was recorded and not accepted; "
        + "start the real TFTP server again to run the backup.";

    /// <summary>
    /// Which socket the refusal leaves from. See <see cref="TftpRefusalSource"/> for why this is a
    /// setting rather than a decision: the controller's answer to it is a bench observation nobody
    /// has made yet, and it is not discoverable any other way.
    /// </summary>
    public TftpRefusalSource RefusalSource { get; init; } = TftpRefusalSource.ListeningSocket;

    /// <summary>
    /// Window within which an identical request from the same source counts as a retransmit.
    ///
    /// <para>Wider than the DHCP side's three seconds. A TFTP client's first retry is typically
    /// after a full timeout period, and RFC 2349's default timeout is five seconds.</para>
    /// </summary>
    public TimeSpan RetransmitWindow { get; init; } = TimeSpan.FromSeconds(6);

    /// <summary>
    /// Refuse to start when something else already holds UDP/69.
    ///
    /// <para>This matters more here than it does on the DHCP side, and in the opposite direction.
    /// On the backup server the process holding UDP/69 <i>is the backup server</i>, and quietly
    /// binding alongside it would mean Windows delivering each request to whichever socket it
    /// felt like - so half the backups would land and half would hit a watch that refuses them.
    /// Stopping the real server is a decision somebody makes deliberately, out loud, not
    /// something this tool arranges by starting.</para>
    /// </summary>
    public bool RefuseOnSeriousPortConflict { get; init; } = true;

    /// <summary>
    /// Where <see cref="TftpWatchMode.Accept"/> writes what it receives, and reads what it sends
    /// back. Required in that mode; ignored in Observe. Nothing is ever written outside it.
    /// </summary>
    public string? AcceptFolder { get; init; }

    /// <summary>
    /// Whether a write may replace a file that already exists. Off by default, the way most TFTP
    /// servers ship - and the classic reason a second backup with the same name is refused, so a
    /// refusal for this reason says so in as many words.
    /// </summary>
    public bool AllowOverwrite { get; init; }

    /// <summary>How long a transfer waits for each packet before sending its last one again.</summary>
    public TimeSpan TransferTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Retries per packet before a transfer is abandoned.</summary>
    public int TransferMaxRetries { get; init; } = TftpTransfer.DefaultMaxRetries;
}
