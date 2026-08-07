using System.Net;
using NetControl.Core.Interfaces;

namespace NetControl.Core.Dhcp;

/// <summary>
/// A reply that was actually put on the wire. Every one of these is a state-changing operation
/// and goes in the append-only event log: timestamp, target, what was sent, over which adapter.
/// </summary>
public sealed class DhcpReplyEventArgs(
    DateTimeOffset timestamp,
    BootpPacket request,
    BootpPacket reply,
    DeviceAssignment? assignment,
    NicInfo nic,
    ReplySendMode sendMode,
    IPEndPoint destination,
    int byteCount) : EventArgs()
{
    public DateTimeOffset Timestamp { get; } = timestamp;

    public BootpPacket Request { get; } = request;

    public BootpPacket Reply { get; } = reply;

    /// <summary>Null for a DHCP NAK, which refuses an address rather than granting one.</summary>
    public DeviceAssignment? Assignment { get; } = assignment;

    public NicInfo Nic { get; } = nic;

    public ReplySendMode SendMode { get; } = sendMode;

    public IPEndPoint Destination { get; } = destination;

    public int ByteCount { get; } = byteCount;

    public MacAddress Mac => Request.ClientMac;

    public string Kind => Reply.MessageType switch
    {
        DhcpMessageType.Offer => "DHCP OFFER",
        DhcpMessageType.Ack => "DHCP ACK",
        DhcpMessageType.Nak => "DHCP NAK",
        null => "BOOTP REPLY",
        _ => "reply",
    };
}
