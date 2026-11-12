namespace NetControl.Core.Safety;

/// <summary>What kind of thing the application is about to put on the wire.</summary>
public enum TransmitKind
{
    /// <summary>A BOOTP/DHCP reply to a device that asked (UDP/68, usually broadcast).</summary>
    DhcpReply,

    /// <summary>An EtherNet/IP ListIdentity request (UDP/44818 broadcast or unicast).</summary>
    ListIdentity,

    /// <summary>A TCP connection to a device: EtherNet/IP/CIP, Modbus/TCP, or a service check.</summary>
    TcpConnect,

    /// <summary>An ICMP echo request.</summary>
    Ping,

    /// <summary>A TFTP datagram - the watch's refusals, a received backup's acknowledgements, a probe.</summary>
    Tftp,

    /// <summary>A raw Ethernet frame through Npcap - PROFINET DCP.</summary>
    RawFrame,

    /// <summary>A change to this PC's own adapter (netsh). Local, but it changes what the network sees.</summary>
    AdapterChange,
}
