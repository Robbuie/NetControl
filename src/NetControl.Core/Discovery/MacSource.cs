namespace NetControl.Core.Discovery;

/// <summary>
/// Where a discovered device's hardware address came from, or why there isn't one.
///
/// <para>This is carried rather than collapsed into "MAC or null" because the three ways of not
/// knowing need three different sentences in front of the user, and a null cannot say which one
/// it is.</para>
/// </summary>
public enum MacSource
{
    /// <summary>Nothing has been resolved yet.</summary>
    None,

    /// <summary>
    /// The ARP cache on the same adapter the scan went out of. This is the ordinary case and the
    /// only one a plan row is built from.
    /// </summary>
    Arp,

    /// <summary>
    /// There is an ARP entry for the address, but another adapter learned it. That is a different
    /// device on a different segment that happens to share an address, or the same device reached
    /// two ways - and neither is safe to key a commissioning plan on, so the MAC is not taken.
    /// </summary>
    ArpOnAnotherAdapter,

    /// <summary>The device answered, and the address is not in the cache.</summary>
    NotInArp,

    /// <summary>The ARP table could not be read at all. Not the same as it being empty.</summary>
    NotChecked,
}
