namespace NetControl.Core;

/// <summary>
/// Every <see cref="DeviceQuirks"/> flag in words, in one place, so the grid cell, the editor and
/// the record cannot describe the same flag three ways.
///
/// <para>The <see cref="DeviceQuirkInfo.Effect"/> line is honest about which quirks change what the
/// tool does and which are a note for the next person. A checkbox that looks like it does something
/// and does not is worse than no checkbox.</para>
/// </summary>
public static class DeviceQuirkCatalog
{
    public static IReadOnlyList<DeviceQuirkInfo> All { get; } =
    [
        new(DeviceQuirks.HardwarePinnedAddress, "Address pinned by switches", "switches",
            "Configuration Capability says the address cannot be set over the network - rotary or DIP switches decide it.",
            "The Quirks column says so before anyone presses Set static, which still asks the device first and refuses before writing."),
        new(DeviceQuirks.RequiresResetToApply, "Needs a reset to apply", "reset",
            "Accepts a new address but keeps using the old one until it is reset or power-cycled.",
            "Set static plans for a reset - and performs one only when 'Allow reset' is ticked."),
        new(DeviceQuirks.LiesAboutWriteSuccess, "Says yes and discards the write", "lies",
            "Answers success to the write and keeps its old configuration.",
            "Informs only: the readback already catches it every time. Worth knowing before trusting another tool."),
        new(DeviceQuirks.SlowResponses, "Slow to answer", "slow",
            "A thin TCP stack that takes seconds to accept a connection or to come back after a change.",
            "Set static waits three times as long to connect and twice as long for the readback."),
        new(DeviceQuirks.DropsConnectionOnWrite, "Drops the connection on a write", "drops",
            "Closes the TCP connection part way through a configuration write.",
            "Informs only: an attempt that loses the connection after writing is already reported unverified, never done."),
        new(DeviceQuirks.RejectsConfigWhileDynamic, "Rejects an address while in BOOTP/DHCP", "order",
            "Refuses the address write until Configuration Control has been set to Static.",
            "Informs only: Set static always writes Configuration Control first, for exactly this reason."),
        new(DeviceQuirks.IgnoresBroadcastDiscovery, "Ignores broadcast discovery", "no-bcast",
            "Does not answer a broadcast ListIdentity, only one sent to its own address.",
            "A scan also asks this device's planned address directly, so it is not missed."),
    ];

    /// <summary>The info for one flag, or null for a value no build of this tool has named.</summary>
    public static DeviceQuirkInfo? For(DeviceQuirks flag) => All.FirstOrDefault(q => q.Flag == flag);

    /// <summary>"reset, slow" for the grid cell; empty when there are none.</summary>
    public static string ShortText(DeviceQuirks quirks) =>
        string.Join(", ", All.Where(q => quirks.HasFlag(q.Flag)).Select(q => q.Short));

    /// <summary>One line per quirk with its meaning, for a tooltip or the record. Empty when there are none.</summary>
    public static string LongText(DeviceQuirks quirks) =>
        string.Join(Environment.NewLine, All.Where(q => quirks.HasFlag(q.Flag)).Select(q => $"{q.Label}: {q.Meaning}"));
}
