namespace NetControl.Core.Dhcp;

/// <summary>Everything about how the server behaves, separated from what it decides to serve.</summary>
public sealed record DhcpServerOptions
{
    /// <summary>The BOOTP/DHCP server port.</summary>
    public int ListenPort { get; init; } = 67;

    /// <summary>
    /// Where replies go. 68 in reality; changed only to test against NetControl.DeviceSim on the
    /// same machine, where the real client port is already taken by Windows.
    /// </summary>
    public int ClientPort { get; init; } = 68;

    public DhcpServerMode Mode { get; init; } = DhcpServerMode.Watch;

    public ReplySendMode SendMode { get; init; } = ReplySendMode.UnicastInterfaceOption;

    /// <summary>
    /// Only handle requests that arrived on this interface index. Null means every adapter.
    /// Requests from other adapters are still reported - being told "your device is asking, but
    /// on the adapter you did not pick" is the single most useful thing this tool can say.
    /// </summary>
    public int? InterfaceIndexFilter { get; init; }

    /// <summary>Window within which an identical (xid, MAC) counts as a retransmit.</summary>
    public TimeSpan RetransmitWindow { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Lease time offered to devices that speak DHCP rather than BOOTP. Long, because a
    /// commissioning session is not a lease-management exercise and a device renewing mid-startup
    /// helps nobody.
    /// </summary>
    public TimeSpan LeaseTime { get; init; } = TimeSpan.FromDays(8);

    /// <summary>
    /// Refuse to start when something else holds the port in a way that makes delivery ambiguous.
    /// A component that cannot do its job must say so and stop, not degrade into a half-working
    /// state - that is the entire complaint about the tool this replaces.
    /// </summary>
    public bool RefuseOnSeriousPortConflict { get; init; } = true;

    /// <summary>
    /// Next server, TFTP server, boot file, domain and DNS for every reply. Empty by default, which
    /// leaves replies exactly as they were before these existed.
    /// </summary>
    public BootOptions Boot { get; init; } = BootOptions.None;

    /// <summary>
    /// Requests handled per client MAC per second. Past this, requests from that MAC are counted and
    /// summarised every few seconds instead of being logged, recorded and answered one by one - a
    /// device in a reboot loop must not be able to fill the log, the project file, or the segment.
    /// </summary>
    public int MaxRequestsPerClientPerSecond { get; init; } = 10;

    /// <summary>
    /// Replies sent per client MAC per second. A DHCP exchange needs two (offer, acknowledge); four
    /// leaves room for a retransmit without ever letting this server become a broadcast storm.
    /// </summary>
    public int MaxRepliesPerClientPerSecond { get; init; } = 4;

    /// <summary>The transmit gate - listen-only and the global rate cap. Null means the shared one.</summary>
    public Safety.NetworkGuard? Guard { get; init; }
}
