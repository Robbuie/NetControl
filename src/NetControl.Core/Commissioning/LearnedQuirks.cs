namespace NetControl.Core.Commissioning;

/// <summary>What one attempt taught about a device, and the evidence for each flag in a sentence.</summary>
/// <param name="Flags">Only the flags the device did not already carry.</param>
/// <param name="Reasons">One sentence per flag, for the event row.</param>
public sealed record LearnedQuirks(DeviceQuirks Flags, IReadOnlyList<string> Reasons)
{
    public bool Any => Flags != DeviceQuirks.None;
}
