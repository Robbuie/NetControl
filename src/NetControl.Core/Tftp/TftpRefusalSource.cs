namespace NetControl.Core.Tftp;

/// <summary>
/// Which socket a refusal leaves from - and therefore what source port the client sees it come
/// from.
///
/// <para>This exists because of one unanswered question, and it is deliberately a setting rather
/// than a decision. RFC 1350 has a server pick a fresh transfer identifier for a transfer it
/// accepts; it says nothing about one it rejects outright, and servers in the field answer an
/// immediate rejection from the well-known port. <b>No FANUC controller has ever been seen to
/// accept either.</b> If one ignores a refusal from port 69 and retransmits until it times out,
/// the fix is to answer from a temporary socket instead - and finding that out is a robot down for
/// an hour, so both behaviours are reachable from the same visit rather than from two.</para>
///
/// <para>When the answer is known, the one that works becomes the default and this enum can go.
/// Until then, neither value is the "right" one and the bench notes decide.</para>
/// </summary>
public enum TftpRefusalSource
{
    /// <summary>
    /// Answer from the socket the request arrived on, so the refusal's source port is the
    /// well-known port. What servers in the field do for an immediate rejection, and the default.
    /// </summary>
    ListeningSocket = 0,

    /// <summary>
    /// Answer from a fresh socket bound to an ephemeral port, so the refusal carries a transfer
    /// identifier of its own - what RFC 1350 describes for a transfer, applied to its rejection.
    ///
    /// <para>The socket is bound to the address the request was delivered to, not to
    /// <c>0.0.0.0</c>: on a multi-homed machine an answer from an address the client never wrote
    /// to is discarded as an unknown transfer id, which looks exactly like silence.</para>
    /// </summary>
    TemporarySocket = 1,
}
