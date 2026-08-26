namespace NetControl.Core.Dhcp;

/// <summary>
/// How to pin a limited broadcast to one adapter. Phase 0 established that
/// <see cref="ReplySendMode.UnicastInterfaceOption"/> works, but only against a listener on the same machine,
/// so the per-socket path stays until real hardware confirms it is unnecessary.
/// </summary>
public enum ReplySendMode
{
    /// <summary>
    /// IP_UNICAST_IF (option 31) on the listening socket. One socket, set per reply. Note the
    /// value is the interface index in NETWORK byte order for IPv4 - get that wrong and it fails
    /// silently, which is the worst possible failure for this particular feature.
    /// </summary>
    UnicastInterfaceOption = 0,

    /// <summary>
    /// A short-lived socket bound to the adapter's own address, so the routing decision is
    /// unambiguous. A bind per reply is irrelevant at commissioning rates.
    /// </summary>
    PerSocketBind = 1,
}
