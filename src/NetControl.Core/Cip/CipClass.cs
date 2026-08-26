namespace NetControl.Core.Cip;

/// <summary>CIP object class ids. Only the three this tool touches.</summary>
public static class CipClass
{
    /// <summary>Identity. Vendor, product name, serial, revision - and the Reset service.</summary>
    public const ushort Identity = 0x01;

    /// <summary>
    /// TCP/IP Interface. Where the address lives, and therefore the whole point of Phase 2:
    /// attribute 2 says whether it can be set, attribute 3 is BOOTP/DHCP/Static, attribute 5 is
    /// the addresses themselves.
    /// </summary>
    public const ushort TcpIpInterface = 0xF5;

    /// <summary>Ethernet Link. Attribute 3 is the MAC, which is how a scan result becomes a plan row.</summary>
    public const ushort EthernetLink = 0xF6;
}
