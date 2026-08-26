namespace NetControl.Core;

/// <summary>
/// Known misbehaviours of a particular device, stored as the <c>Device.QuirkFlags</c> column.
///
/// The commissioner in Phase 2 uses these to sequence its writes; Phase 1 only carries them so
/// that the column has a meaning from the first project file rather than being an opaque integer
/// somebody has to reverse-engineer later. The values deliberately mirror
/// <c>NetControl.DeviceSim</c>'s quirk set: every quirk found on real hardware gets a simulator
/// scenario and a regression test, which is what stops a fix for one vendor breaking another.
///
/// Values are persisted. Append new flags; never renumber an existing one.
/// </summary>
[Flags]
public enum DeviceQuirks : long
{
    None = 0,

    /// <summary>
    /// Rejects a write to TCP/IP Interface Object (0xF5) attribute 5 while the device is still in
    /// BOOTP/DHCP mode. This is why the sequence sets attribute 3 to Static first.
    /// </summary>
    RejectsConfigWhileDynamic = 1L << 0,

    /// <summary>
    /// Address is pinned by rotary or DIP switches: Configuration Capability reports
    /// ConfigurationSettable clear and every write is refused. Nothing this tool does will help;
    /// somebody has to open the panel.
    /// </summary>
    HardwarePinnedAddress = 1L << 1,

    /// <summary>
    /// Accepts the write but holds it pending until an Identity reset or a power cycle. Reads
    /// before the reset still report the old values, so a readback alone would look like failure.
    /// </summary>
    RequiresResetToApply = 1L << 2,

    /// <summary>
    /// Returns a success status for the write and silently discards it. The readback is the only
    /// thing that catches this, which is exactly why the sequence always reads back.
    /// </summary>
    LiesAboutWriteSuccess = 1L << 3,

    /// <summary>Thin, slow TCP stack. Needs longer timeouts and gentler concurrency.</summary>
    SlowResponses = 1L << 4,

    /// <summary>Drops the TCP connection partway through a set operation.</summary>
    DropsConnectionOnWrite = 1L << 5,

    /// <summary>Does not answer broadcast ListIdentity, only unicast.</summary>
    IgnoresBroadcastDiscovery = 1L << 6,
}
