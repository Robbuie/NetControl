namespace NetControl.Core.Dhcp;

/// <summary>
/// DHCP option 53. Its absence is meaningful: a message with no option 53 is plain RFC 951
/// BOOTP, and answering it with DHCP options is how you get a device that ignores the reply.
/// Rockwell adapters ship DHCP-enabled from the factory more often than people expect, so both
/// dialects have to work.
/// </summary>
public enum DhcpMessageType : byte
{
    Discover = 1,
    Offer = 2,
    Request = 3,
    Decline = 4,
    Ack = 5,
    Nak = 6,
    Release = 7,
    Inform = 8,
}
