namespace NetControl.Core.Reporting;

/// <summary>
/// Where a device's commissioning stands according to the record - the last Set static or hand-back
/// that finished, read back out of the append-only event log.
/// </summary>
public enum CommissioningOutcome
{
    /// <summary>Nothing has been written to it, as far as the record knows.</summary>
    None,

    /// <summary>The last write was read back off the device holding the planned address, static.</summary>
    Verified,

    /// <summary>The last attempt did not verify. The message says how far it got.</summary>
    NotVerified,

    /// <summary>The last operation handed it back to BOOTP or DHCP, verified. Not commissioned any more.</summary>
    HandedBack,
}
