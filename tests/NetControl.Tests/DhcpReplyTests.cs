using System.Net;
using System.Text;
using NetControl.Core;
using NetControl.Core.Dhcp;
using Xunit;

namespace NetControl.Tests;

/// <summary>
/// <see cref="DhcpServer.BuildReply"/> is static and side-effect free precisely so it can be
/// tested here, with no socket anywhere near it.
/// </summary>
public class DhcpReplyTests
{
    private const uint Lease = 8 * 24 * 3600;

    private static readonly IPAddress ServerIp = IPAddress.Parse("192.168.1.10");

    private static DeviceAssignment Planned => new(
        MacAddress.Parse(Frames.DeviceMac),
        IPAddress.Parse("192.168.1.51"),
        IPAddress.Parse("255.255.255.0"),
        IPAddress.Parse("192.168.1.1"));

    private static BootpPacket Parse(string hex)
    {
        Assert.True(BootpPacket.TryParse(Frames.Bytes(hex), out BootpPacket? packet));
        return packet!;
    }

    [Fact]
    public void AnswersPlainBootpWithoutDhcpSemantics()
    {
        BootpPacket request = Parse(Frames.BootpRequest);

        BootpPacket reply = Assert.IsType<BootpPacket>(DhcpServer.BuildReply(request, Planned, ServerIp, Lease));

        Assert.Equal(BootpPacket.OpBootReply, reply.Op);
        Assert.Equal(IPAddress.Parse("192.168.1.51"), reply.YourIp);
        Assert.Equal(ServerIp, reply.ServerIp);
        Assert.Equal(request.TransactionId, reply.TransactionId);
        Assert.Equal(request.Flags, reply.Flags);
        Assert.Equal(request.ClientMac, reply.ClientMac);

        // Giving a BOOTP client DHCP semantics is a good way to get the reply ignored.
        Assert.Null(reply.MessageType);
        Assert.False(reply.Options.ContainsKey(DhcpOptionCode.ServerIdentifier));

        // Mask and router are RFC 1048 vendor extensions and perfectly welcome.
        Assert.Equal(IPAddress.Parse("255.255.255.0"), reply.GetAddressOption(DhcpOptionCode.SubnetMask));
        Assert.Equal(IPAddress.Parse("192.168.1.1"), reply.GetAddressOption(DhcpOptionCode.Router));
    }

    [Fact]
    public void AnswersDiscoverWithAnOffer()
    {
        BootpPacket request = Parse(Frames.DhcpDiscover);

        BootpPacket reply = Assert.IsType<BootpPacket>(DhcpServer.BuildReply(request, Planned, ServerIp, Lease));

        Assert.Equal(DhcpMessageType.Offer, reply.MessageType);
        Assert.Equal(IPAddress.Parse("192.168.1.51"), reply.YourIp);
        Assert.Equal(ServerIp, reply.GetAddressOption(DhcpOptionCode.ServerIdentifier));
        Assert.Equal(IPAddress.Parse("255.255.255.0"), reply.GetAddressOption(DhcpOptionCode.SubnetMask));
        Assert.Equal(
            new byte[] { 0x00, 0x0A, 0x8C, 0x00 },
            reply.Options[DhcpOptionCode.IpAddressLeaseTime]);
    }

    [Fact]
    public void AnswersARequestForThePlannedAddressWithAnAck()
    {
        BootpPacket request = Parse(Frames.DhcpDiscover);
        request.SetOption(DhcpOptionCode.DhcpMessageType, DhcpMessageType.Request);
        request.SetOption(DhcpOptionCode.RequestedIpAddress, IPAddress.Parse("192.168.1.51"));

        BootpPacket reply = Assert.IsType<BootpPacket>(DhcpServer.BuildReply(request, Planned, ServerIp, Lease));

        Assert.Equal(DhcpMessageType.Ack, reply.MessageType);
        Assert.Equal(IPAddress.Parse("192.168.1.51"), reply.YourIp);
    }

    /// <summary>
    /// The device was re-planned since it last booted and is asking for its old address. ACKing a
    /// different address than the one requested leaves it looping forever; a NAK makes it start
    /// over and take the new one.
    /// </summary>
    [Fact]
    public void NaksARequestForAnAddressThatIsNotThePlan()
    {
        BootpPacket request = Parse(Frames.DhcpRequestWrongIp);
        Assert.Equal(IPAddress.Parse("192.168.1.99"), request.GetAddressOption(DhcpOptionCode.RequestedIpAddress));

        BootpPacket reply = Assert.IsType<BootpPacket>(DhcpServer.BuildReply(request, Planned, ServerIp, Lease));

        Assert.Equal(DhcpMessageType.Nak, reply.MessageType);
        Assert.Equal(IPAddress.Any, reply.YourIp);
        Assert.False(reply.Options.ContainsKey(DhcpOptionCode.SubnetMask));
        Assert.Contains(
            "192.168.1.51",
            Encoding.ASCII.GetString(reply.Options[DhcpOptionCode.Message]),
            StringComparison.Ordinal);
    }

    [Fact]
    public void TreatsCiaddrAsTheRequestedAddressWhenOption50IsAbsent()
    {
        BootpPacket request = Parse(Frames.DhcpDiscover);
        request.SetOption(DhcpOptionCode.DhcpMessageType, DhcpMessageType.Request);
        request.ClientIp = IPAddress.Parse("192.168.1.99");

        BootpPacket reply = Assert.IsType<BootpPacket>(DhcpServer.BuildReply(request, Planned, ServerIp, Lease));

        Assert.Equal(DhcpMessageType.Nak, reply.MessageType);
    }

    [Theory]
    [InlineData(DhcpMessageType.Release)]
    [InlineData(DhcpMessageType.Decline)]
    [InlineData(DhcpMessageType.Inform)]
    public void SaysNothingWhenSilenceIsTheCorrectAnswer(DhcpMessageType type)
    {
        BootpPacket request = Parse(Frames.DhcpDiscover);
        request.SetOption(DhcpOptionCode.DhcpMessageType, type);

        Assert.Null(DhcpServer.BuildReply(request, Planned, ServerIp, Lease));
    }

    [Fact]
    public void SendsARelayedReplyBackThroughTheRelay()
    {
        BootpPacket request = Parse(Frames.BootpRequest);
        request.GatewayIp = IPAddress.Parse("192.168.1.254");

        BootpPacket reply = Assert.IsType<BootpPacket>(DhcpServer.BuildReply(request, Planned, ServerIp, Lease));

        Assert.Equal(IPAddress.Parse("192.168.1.254"), reply.GatewayIp);
    }

    [Fact]
    public void OmitsTheRouterOptionWhenThePlanHasNoGateway()
    {
        var noGateway = new DeviceAssignment(
            MacAddress.Parse(Frames.DeviceMac),
            IPAddress.Parse("192.168.1.51"),
            IPAddress.Parse("255.255.255.0"));

        BootpPacket reply = Assert.IsType<BootpPacket>(
            DhcpServer.BuildReply(Parse(Frames.BootpRequest), noGateway, ServerIp, Lease));

        Assert.False(reply.Options.ContainsKey(DhcpOptionCode.Router));
    }
}
