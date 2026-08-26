using System.Net;
using NetControl.Core;
using NetControl.Core.Dhcp;
using NetControl.Core.Interfaces;

namespace NetControl.Tests;

/// <summary>
/// Builds the event args a running server would raise, so a view model test can be driven with
/// the real types rather than a stand-in that might drift from them.
/// </summary>
internal static class Events
{
    public static readonly DateTimeOffset At = new(2026, 8, 7, 9, 30, 0, TimeSpan.Zero);

    public static BootpPacket Packet(MacAddress mac, uint transactionId = 0x1234ABCD) => new()
    {
        Op = BootpPacket.OpBootRequest,
        TransactionId = transactionId,
        ClientMac = mac,
    };

    public static DhcpRequestEventArgs Request(
        MacAddress mac,
        AssignmentDecision decision,
        NicInfo? nic = null,
        uint transactionId = 0x1234ABCD,
        bool isRetransmit = false,
        int? arrivalIndex = null,
        DateTimeOffset? at = null)
    {
        NicInfo arrival = nic ?? Nics.Healthy();

        return new DhcpRequestEventArgs(
            at ?? At,
            Packet(mac, transactionId),
            arrivalIndex ?? arrival.Index,
            arrival,
            IPAddress.Broadcast,
            isRetransmit,
            decision);
    }

    public static DhcpReplyEventArgs Reply(DeviceAssignment assignment, NicInfo? nic = null)
    {
        NicInfo arrival = nic ?? Nics.Healthy();
        BootpPacket request = Packet(assignment.Mac);

        var reply = new BootpPacket
        {
            Op = BootpPacket.OpBootReply,
            TransactionId = request.TransactionId,
            ClientMac = assignment.Mac,
            YourIp = assignment.Ip,
        };

        return new DhcpReplyEventArgs(
            At,
            request,
            reply,
            assignment,
            arrival,
            ReplySendMode.PerSocketBind,
            new IPEndPoint(IPAddress.Broadcast, 68),
            300);
    }

    public static DeviceAssignment Assignment(MacAddress mac, string ip = "192.168.1.51") =>
        new(mac, IPAddress.Parse(ip), IPAddress.Parse("255.255.255.0"));
}
