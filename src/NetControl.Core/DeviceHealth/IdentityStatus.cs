namespace NetControl.Core.DeviceHealth;

/// <summary>
/// Identity object (class 0x01) attribute 5, the device status word, decoded. The same word arrives
/// in a ListIdentity reply; this is the one read directly off the device.
///
/// <para>The four fault bits are what a module's own status LED is summarising, and reading them
/// from a laptop saves a walk to a cabinet to find out which LED is flashing red.</para>
/// </summary>
public readonly record struct IdentityStatus(ushort Raw)
{
    /// <summary>Bit 0: something holds an exclusive-owner connection to this device - a controller is talking to it.</summary>
    public bool Owned => (Raw & 0x0001) != 0;

    /// <summary>Bit 2: the device has been configured away from its out-of-box defaults.</summary>
    public bool Configured => (Raw & 0x0004) != 0;

    /// <summary>Bits 4-7.</summary>
    public int ExtendedStatus => (Raw >> 4) & 0x0F;

    public bool MinorRecoverableFault => (Raw & 0x0100) != 0;

    public bool MinorUnrecoverableFault => (Raw & 0x0200) != 0;

    public bool MajorRecoverableFault => (Raw & 0x0400) != 0;

    public bool MajorUnrecoverableFault => (Raw & 0x0800) != 0;

    public bool HasMajorFault => MajorRecoverableFault || MajorUnrecoverableFault;

    public bool HasMinorFault => MinorRecoverableFault || MinorUnrecoverableFault;

    /// <summary>The extended status in words. Vendor-specific values are said to be so.</summary>
    public string ExtendedStatusText => ExtendedStatus switch
    {
        0 => "self-testing or unknown",
        1 => "firmware update in progress",
        2 => "at least one I/O connection faulted",
        3 => "no I/O connections established",
        4 => "non-volatile configuration bad",
        5 => "major fault",
        6 => "at least one I/O connection in run mode",
        7 => "I/O connections established, all in idle mode",
        >= 10 => $"vendor-specific status {ExtendedStatus}",
        _ => $"reserved status {ExtendedStatus}",
    };

    public override string ToString()
    {
        var parts = new List<string> { ExtendedStatusText };

        if (Owned)
        {
            parts.Add("owned");
        }

        if (MajorUnrecoverableFault)
        {
            parts.Add("MAJOR UNRECOVERABLE FAULT");
        }
        else if (MajorRecoverableFault)
        {
            parts.Add("major recoverable fault");
        }

        if (MinorUnrecoverableFault)
        {
            parts.Add("minor unrecoverable fault");
        }
        else if (MinorRecoverableFault)
        {
            parts.Add("minor recoverable fault");
        }

        return string.Join(", ", parts);
    }
}
