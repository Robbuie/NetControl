namespace NetControl.Core.Discovery;

/// <summary>What changed about one device between two scans of the same subnet.</summary>
public enum InventoryChangeKind
{
    /// <summary>The same device, at a different address. Something re-addressed it.</summary>
    Moved,

    /// <summary>The same product at the same address, with a different serial. A module was swapped.</summary>
    Replaced,

    /// <summary>A different product entirely at an address something else held last time.</summary>
    DifferentDevice,

    /// <summary>The same device, reporting a different firmware revision.</summary>
    FirmwareChanged,

    /// <summary>Not seen in the earlier scan.</summary>
    New,

    /// <summary>Answered last time, silent now. Silence, not absence: it may be powered down.</summary>
    NotAnswering,
}
