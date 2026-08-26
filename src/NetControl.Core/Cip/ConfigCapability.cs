namespace NetControl.Core.Cip;

/// <summary>
/// TCP/IP Interface Object (0xF5) attribute 2, Configuration Capability - what the device will
/// let you do to its address.
///
/// <para><see cref="ConfigurationSettable"/> is the one that earns its keep. Reading it before
/// attempting anything turns "the write mysteriously failed" into "the address is pinned by the
/// switches on the module", which is a different quality of answer at the end of a long day.</para>
/// </summary>
[Flags]
public enum ConfigCapability : uint
{
    None = 0,

    BootpClient = 1 << 0,

    DnsClient = 1 << 1,

    DhcpClient = 1 << 2,

    DhcpDnsUpdate = 1 << 3,

    /// <summary>
    /// The interface configuration can be set over the network at all. Clear means rotary or DIP
    /// switches decide the address and no amount of retrying will change that.
    /// </summary>
    ConfigurationSettable = 1 << 4,

    /// <summary>Address is currently taken from hardware switches.</summary>
    HardwareConfigurable = 1 << 5,

    /// <summary>A written configuration does not take effect until the interface is reset.</summary>
    InterfaceResetNeeded = 1 << 6,

    /// <summary>Address Conflict Detection.</summary>
    AcdCapable = 1 << 7,
}
