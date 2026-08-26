namespace NetControl.Core.Commissioning;

/// <summary>
/// Where a commissioning run has got to. Every step raises progress and every step is written to
/// the event log - this puts packets on live plant equipment, so the record has to say what was
/// sent and what came back at each point, not just how it finished.
/// </summary>
public enum CommissionStep
{
    Connect,

    /// <summary>Attribute 2. Always first, and sometimes the last thing that happens.</summary>
    ReadCapability,

    /// <summary>Attribute 3 to Static. This is the step that disables BOOTP/DHCP.</summary>
    WriteConfigMethod,

    /// <summary>Attribute 5, the addresses.</summary>
    WriteConfiguration,

    /// <summary>Identity Reset. Only ever sent when the caller explicitly allowed it.</summary>
    Reset,

    /// <summary>Reconnect and read both attributes back off the device.</summary>
    Verify,

    Finished,
}
