using NetControl.Core.Dhcp;

namespace NetControl.Core.Enip;

/// <summary>
/// How a ListIdentity scan behaves on the wire.
///
/// <para>Every default here is the gentle one. Industrial devices have thin stacks and some fall
/// over under traffic a PC would not notice, so anything faster or wider than these values is a
/// decision somebody makes deliberately and can be held to.</para>
/// </summary>
public sealed record ScanOptions
{
    /// <summary>EtherNet/IP's registered port. ListIdentity is the UDP half of it.</summary>
    public int Port { get; init; } = EnipSession.DefaultPort;

    /// <summary>
    /// How long to keep listening after the last probe goes out. Two seconds is comfortably longer
    /// than any adapter takes to answer and short enough that a user presses the button again
    /// rather than wondering whether it worked.
    /// </summary>
    public TimeSpan Window { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Whether to send the one broadcast. Off means the scan probes only the addresses it was
    /// handed - which is what a test does, because a test must never put discovery traffic on
    /// whatever network the build machine happens to be plugged into.
    /// </summary>
    public bool Broadcast { get; init; } = true;

    /// <summary>
    /// Gap between unicast probes. At 25ms a full /24 sweep takes about six seconds and puts one
    /// small datagram at a time on the wire, which is the difference between a sweep and a scan
    /// somebody's PLC notices.
    /// </summary>
    public TimeSpan UnicastPace { get; init; } = TimeSpan.FromMilliseconds(25);

    /// <summary>
    /// How the outgoing datagram is pinned to the chosen adapter. Same question, and the same open
    /// answer, as the DHCP server's reply path - see <see cref="ReplySendMode"/>. Binding the
    /// socket to the adapter's own address is the default here because the scan opens a socket of
    /// its own anyway, so the unambiguous option is also the free one.
    /// </summary>
    public ReplySendMode SendMode { get; init; } = ReplySendMode.PerSocketBind;
}
