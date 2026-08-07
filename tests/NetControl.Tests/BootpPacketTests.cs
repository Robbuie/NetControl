using System.Net;
using NetControl.Core;
using NetControl.Core.Dhcp;
using Xunit;

namespace NetControl.Tests;

public class BootpPacketTests
{
    [Fact]
    public void ParsesClassicBootpRequestWithNoOptions()
    {
        Assert.True(BootpPacket.TryParse(Frames.Bytes(Frames.BootpRequest), out BootpPacket? packet));
        Assert.NotNull(packet);

        Assert.Equal(BootpPacket.OpBootRequest, packet!.Op);
        Assert.True(packet.IsRequest);
        Assert.Equal(0xFC5D1465u, packet.TransactionId);
        Assert.Equal(MacAddress.Parse(Frames.DeviceMac), packet.ClientMac);
        Assert.True(packet.WantsBroadcastReply);

        // No magic cookie means no options, and no option 53 means this is BOOTP, not DHCP.
        Assert.Empty(packet.Options);
        Assert.Null(packet.MessageType);
    }

    [Fact]
    public void ParsesDhcpDiscoverIncludingTheOptionsThatIdentifyTheDevice()
    {
        Assert.True(BootpPacket.TryParse(Frames.Bytes(Frames.DhcpDiscover), out BootpPacket? packet));
        Assert.NotNull(packet);

        Assert.Equal(DhcpMessageType.Discover, packet!.MessageType);
        Assert.Equal("Rockwell Automation, 1756-EN2T", packet.VendorClass);
        Assert.Equal("ENBT-A1", packet.HostName);
        Assert.False(packet.HasOptionOverload);
    }

    [Fact]
    public void ReadsTheTransactionIdBigEndian()
    {
        // The whole BOOTP header is big-endian while everything on the CIP side of this product
        // is little-endian, so this assertion is guarding against the most expensive mistake
        // available in this codebase.
        byte[] frame = Frames.Bytes(Frames.BootpRequest);
        Assert.Equal(new byte[] { 0xFC, 0x5D, 0x14, 0x65 }, frame[4..8]);

        Assert.True(BootpPacket.TryParse(frame, out BootpPacket? packet));
        Assert.Equal(0xFC5D1465u, packet!.TransactionId);
    }

    [Theory]
    [InlineData(Frames.BootpRequest)]
    [InlineData(Frames.DhcpDiscover)]
    [InlineData(Frames.DhcpRequestWrongIp)]
    public void RoundTripsEveryFieldWeCareAbout(string hex)
    {
        Assert.True(BootpPacket.TryParse(Frames.Bytes(hex), out BootpPacket? first));
        Assert.True(BootpPacket.TryParse(first!.Serialize(), out BootpPacket? second));
        Assert.NotNull(second);

        Assert.Equal(first.Op, second!.Op);
        Assert.Equal(first.HardwareType, second.HardwareType);
        Assert.Equal(first.HardwareAddressLength, second.HardwareAddressLength);
        Assert.Equal(first.TransactionId, second.TransactionId);
        Assert.Equal(first.Seconds, second.Seconds);
        Assert.Equal(first.Flags, second.Flags);
        Assert.Equal(first.ClientIp, second.ClientIp);
        Assert.Equal(first.YourIp, second.YourIp);
        Assert.Equal(first.ServerIp, second.ServerIp);
        Assert.Equal(first.GatewayIp, second.GatewayIp);
        Assert.Equal(first.ClientMac, second.ClientMac);
        Assert.Equal(first.MessageType, second.MessageType);
        Assert.Equal(first.Options.Count, second.Options.Count);

        foreach ((byte code, byte[] value) in first.Options)
        {
            Assert.Equal(value, second.Options[code]);
        }
    }

    [Fact]
    public void PadsRepliesToTheRfc1542Minimum()
    {
        // Some older adapters drop short replies, which presents as a device that requests
        // forever and never takes the address.
        var reply = new BootpPacket { Op = BootpPacket.OpBootReply, YourIp = IPAddress.Parse("192.168.1.51") };

        Assert.Equal(BootpPacket.MinimumMessageLength, reply.Serialize().Length);
    }

    [Fact]
    public void GrowsBeyondTheMinimumWhenTheOptionsNeedIt()
    {
        var reply = new BootpPacket { Op = BootpPacket.OpBootReply };
        for (byte code = 100; code < 160; code++)
        {
            reply.SetOption(code, new byte[32]);
        }

        byte[] bytes = reply.Serialize();

        Assert.True(bytes.Length > BootpPacket.MinimumMessageLength);
        Assert.True(BootpPacket.TryParse(bytes, out BootpPacket? parsed));
        Assert.Equal(60, parsed!.Options.Count);
    }

    [Fact]
    public void RefusesAnOptionTooLongForItsLengthByte()
    {
        var reply = new BootpPacket { Op = BootpPacket.OpBootReply };
        reply.SetOption(200, new byte[256]);

        Assert.Throws<InvalidOperationException>(() => reply.Serialize());
    }

    [Fact]
    public void KeepsWhatItCouldParseWhenTheOptionsAreTruncated()
    {
        // A device with a slightly wrong options encoding is still a device we want in the log.
        byte[] full = Frames.Bytes(Frames.DhcpDiscover);
        byte[] cut = full[..(BootpPacket.FixedHeaderLength + 4 + 3)];   // cookie + option 53 only

        Assert.True(BootpPacket.TryParse(cut, out BootpPacket? packet));
        Assert.Equal(DhcpMessageType.Discover, packet!.MessageType);
    }

    [Fact]
    public void RejectsADatagramTooShortToBeBootp()
    {
        Assert.False(BootpPacket.TryParse(new byte[BootpPacket.FixedHeaderLength - 1], out BootpPacket? packet));
        Assert.Null(packet);
    }

    [Fact]
    public void RejectsAHardwareLengthThatWouldOverrunTheField()
    {
        byte[] frame = Frames.Bytes(Frames.BootpRequest);
        frame[2] = 17;   // chaddr is 16 bytes

        Assert.False(BootpPacket.TryParse(frame, out _));
    }

    [Fact]
    public void TruncatesRatherThanThrowsOnAnOverlongHostName()
    {
        var reply = new BootpPacket
        {
            Op = BootpPacket.OpBootReply,
            ServerHostName = new string('x', 200),
        };

        Assert.True(BootpPacket.TryParse(reply.Serialize(), out BootpPacket? parsed));
        Assert.Equal(63, parsed!.ServerHostName.Length);   // 64-byte field, null terminated
    }
}
