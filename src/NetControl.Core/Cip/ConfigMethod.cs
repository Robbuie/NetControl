namespace NetControl.Core.Cip;

/// <summary>
/// TCP/IP Interface Object (0xF5) attribute 3, Configuration Control - the low nibble.
///
/// <para>This one value is what the Rockwell tool's "Disable BOOTP/DHCP" button writes, and it is
/// the difference between a device that keeps its address and one that asks for it again after the
/// next power cycle. Serving an address is temporary until this says <see cref="Static"/>.</para>
/// </summary>
public enum ConfigMethod : uint
{
    /// <summary>The address is the device's own and survives a power cycle.</summary>
    Static = 0,

    /// <summary>Asks by BOOTP at every startup. What a factory-fresh adapter does.</summary>
    Bootp = 1,

    Dhcp = 2,
}
