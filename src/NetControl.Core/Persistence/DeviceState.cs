namespace NetControl.Core.Persistence;

/// <summary>
/// How far a planned device has got through commissioning.
///
/// <para>A type rather than a string because this is the seam Phase 2's commissioning state
/// machine plugs into. "Served" and "Verified" are the two that matter and they are genuinely
/// different claims: a reply left the adapter, versus the device came up at that address and said
/// so. Conflating them is exactly the failure this tool exists to stop - a CIP success status
/// means the request was accepted, not that the configuration persisted, and a BOOTP reply
/// reaching the wire means even less.</para>
///
/// <para>Ordered by progress, so a grid can sort on it and mean something.</para>
/// </summary>
public enum DeviceState
{
    /// <summary>In the plan. Nothing has been heard from it.</summary>
    Planned,

    /// <summary>Has asked for an address during this session. Passive discovery found it.</summary>
    Seen,

    /// <summary>A reply carrying its planned address reached the wire, and there is an <c>Assignment</c> row.</summary>
    Served,

    /// <summary>
    /// Confirmed to be up at its planned address by reading it back. Nothing sets this yet - the
    /// CIP readback is Phase 2, and an active ListIdentity scan is the cheap version of it. The
    /// member exists so the distinction is in the type from the first project file rather than
    /// being retrofitted once somebody has trusted "Served" to mean this.
    /// </summary>
    Verified,
}
