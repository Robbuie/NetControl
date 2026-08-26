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
}
