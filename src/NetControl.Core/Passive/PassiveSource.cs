namespace NetControl.Core.Passive;

/// <summary>How a device was heard. Flags, because one device is usually heard several ways.</summary>
[Flags]
public enum PassiveSource
{
    None = 0,

    /// <summary>An ARP request or reply: the most reliable MAC-to-address pairing there is.</summary>
    Arp = 1 << 0,

    /// <summary>A BOOTP or DHCP request: a device asking for an address.</summary>
    Dhcp = 1 << 1,

    /// <summary>LLDP: a switch or a device announcing its name and port.</summary>
    Lldp = 1 << 2,

    /// <summary>A PROFINET DCP identify response or hello.</summary>
    ProfinetDcp = 1 << 3,

    /// <summary>PROFINET cyclic I/O frames - a device in data exchange with a controller.</summary>
    ProfinetIo = 1 << 4,

    /// <summary>An EtherNet/IP ListIdentity reply, somebody else's scan.</summary>
    EnipIdentity = 1 << 5,

    /// <summary>EtherNet/IP implicit (UDP 2222) I/O - a device in a connection with a controller.</summary>
    EnipIo = 1 << 6,

    /// <summary>Any other IPv4 traffic from the device.</summary>
    IPv4 = 1 << 7,
}
