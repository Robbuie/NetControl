namespace NetControl.Core.Interfaces;

/// <summary>
/// Raised when the adapter inventory actually changed - not merely when Windows said something
/// happened. The distinction matters: Windows emits several address-change notifications for one
/// cable event, and an interface bar that flickers is an interface bar nobody trusts.
/// </summary>
public sealed class NicsChangedEventArgs(IReadOnlyList<NicInfo> current, IReadOnlyList<NicInfo> previous)
    : EventArgs()
{
    public IReadOnlyList<NicInfo> Current { get; } = current;

    public IReadOnlyList<NicInfo> Previous { get; } = previous;

    /// <summary>Adapters present now that were not present before, matched on interface index.</summary>
    public IReadOnlyList<NicInfo> Added { get; } =
        current.Where(c => previous.All(p => p.Index != c.Index)).ToList();

    /// <summary>Adapters that have gone away - an unplugged dongle, a VPN going down.</summary>
    public IReadOnlyList<NicInfo> Removed { get; } =
        previous.Where(p => current.All(c => c.Index != p.Index)).ToList();

    /// <summary>Same interface index, different address or link state. The common case.</summary>
    public IReadOnlyList<NicInfo> Changed { get; } =
        current.Where(c => previous.Any(p => p.Index == c.Index && p != c)).ToList();
}
