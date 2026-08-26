namespace NetControl.Core.Interfaces;

/// <summary>
/// The read-only view of the adapter inventory that the engine actually needs.
///
/// <see cref="NicMonitor"/> is the real implementation and it does real work - it enumerates the
/// machine, caches, and subscribes to Windows change notifications. None of that is something a
/// test of the receive loop wants to depend on: a datagram sent over loopback arrives on the
/// loopback interface, which the inventory deliberately excludes, so the server would have no
/// adapter to attribute it to. Depending on this interface instead lets a test hand the server a
/// synthetic adapter and exercise the socket path for real.
/// </summary>
public interface INicInventory
{
    /// <summary>Current inventory, ordered by interface index.</summary>
    IReadOnlyList<NicInfo> Snapshot();

    /// <summary>
    /// The adapter with this interface index, or null if it is unknown - normally because the
    /// adapter went away between a packet arriving and this lookup.
    /// </summary>
    NicInfo? ByIndex(int index);
}
