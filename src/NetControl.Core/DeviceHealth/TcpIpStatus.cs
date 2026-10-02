namespace NetControl.Core.DeviceHealth;

/// <summary>
/// TCP/IP Interface object (class 0xF5) attribute 1, Status, decoded.
///
/// <para>Two bits in here bear directly on commissioning and are read by nothing else in the tool:
/// <see cref="ConfigurationPending"/> - an address has been written and has not taken effect - and
/// <see cref="ConflictDetected"/> - the device's own address conflict detection has found something
/// else on its address.</para>
/// </summary>
public readonly record struct TcpIpStatus(uint Raw)
{
    /// <summary>Bits 0-3: where the configuration came from.</summary>
    public int ConfigurationStatus => (int)(Raw & 0x0F);

    /// <summary>Bit 4: a multicast configuration change is waiting for a reset.</summary>
    public bool MulticastPending => (Raw & 0x10) != 0;

    /// <summary>
    /// Bit 5: Interface Configuration Pending. A configuration has been written and the device will
    /// not use it until it is reset or power-cycled. Set static reports this device "unverified",
    /// and this is the bit that says why.
    /// </summary>
    public bool ConfigurationPending => (Raw & 0x20) != 0;

    /// <summary>
    /// Bit 6: ACD status. The device's address conflict detection has seen another station on its
    /// address. Optional in the spec; a device without ACD always reports it clear.
    /// </summary>
    public bool ConflictDetected => (Raw & 0x40) != 0;

    /// <summary>Bit 7: ACD fault - the conflict was bad enough that the device gave up its address.</summary>
    public bool ConflictFault => (Raw & 0x80) != 0;

    public string ConfigurationStatusText => ConfigurationStatus switch
    {
        0 => "not configured",
        1 => "configured from BOOTP, DHCP or stored settings",
        2 => "configured from hardware switches",
        _ => $"reserved configuration status {ConfigurationStatus}",
    };

    public override string ToString() =>
        ConfigurationStatusText
        + (ConfigurationPending ? ", configuration pending a reset" : string.Empty)
        + (ConflictDetected ? ", ADDRESS CONFLICT DETECTED" : string.Empty)
        + (ConflictFault ? ", address conflict fault" : string.Empty);
}
