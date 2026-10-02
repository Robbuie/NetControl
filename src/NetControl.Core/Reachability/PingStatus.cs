namespace NetControl.Core.Reachability;

/// <summary>
/// How one echo request went.
///
/// <para><see cref="TimedOut"/> and <see cref="Unreachable"/> are kept apart on purpose. "Nothing
/// answered" may be the device; "this PC has no route there" is the laptop - wrong adapter, wrong
/// subnet, cable out - and sends somebody to a different place.</para>
/// </summary>
public enum PingStatus
{
    /// <summary>An echo reply came back.</summary>
    Replied,

    /// <summary>
    /// Sent, and nothing came back in time - or, on this PC's own subnet, nothing answered ARP. The
    /// device may be off, or may drop ICMP.
    /// </summary>
    TimedOut,

    /// <summary>This PC, or a router on the way, said there is no route to that network.</summary>
    Unreachable,

    /// <summary>The ping could not be sent at all. <see cref="PingOutcome.Detail"/> says why.</summary>
    Failed,
}
