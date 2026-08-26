using System.Net;
using NetControl.Core.Interfaces;

namespace NetControl.Core.Dhcp;

/// <summary>
/// One BOOTP/DHCP request as it landed, with everything needed to explain what happened to it.
///
/// This is raised for every well-formed request, including ones we decline to answer and ones
/// that arrived on an adapter the user did not select. Silence is the enemy: "a device is asking,
/// but on [12] vEthernet (Default Switch)" is the answer to the question people actually have.
/// </summary>
public sealed class DhcpRequestEventArgs(
    DateTimeOffset timestamp,
    BootpPacket packet,
    int arrivalInterfaceIndex,
    NicInfo? arrivalNic,
    IPAddress destinationAddress,
    bool isRetransmit,
    AssignmentDecision decision) : EventArgs()
{
    public DateTimeOffset Timestamp { get; } = timestamp;

    public BootpPacket Packet { get; } = packet;

    /// <summary>
    /// From IP_PKTINFO. This is the value the whole "wrong adapter" class of failure hinges on,
    /// so it is reported even when the adapter itself could not be resolved.
    /// </summary>
    public int ArrivalInterfaceIndex { get; } = arrivalInterfaceIndex;

    public NicInfo? ArrivalNic { get; } = arrivalNic;

    /// <summary>Usually 255.255.255.255. Worth logging: a unicast request means a renewing client.</summary>
    public IPAddress DestinationAddress { get; } = destinationAddress;

    /// <summary>
    /// The same (xid, MAC) arrived within the retransmit window. The reply is still sent; this
    /// exists so the log can collapse the repeat into a counter instead of a new row.
    /// </summary>
    public bool IsRetransmit { get; } = isRetransmit;

    public AssignmentDecision Decision { get; } = decision;

    public MacAddress Mac => Packet.ClientMac;

    /// <summary>"BOOTP REQUEST" or "DHCP Discover" - what the log line should call this.</summary>
    public string Kind => Packet.MessageType is { } type ? $"DHCP {type}" : "BOOTP REQUEST";
}
