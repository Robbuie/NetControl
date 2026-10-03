using System.Buffers.Binary;
using System.Net;
using NetControl.Core;
using NetControl.Core.Capture;
using NetControl.Core.Passive;
using Xunit;

namespace NetControl.Tests;

/// <summary>
/// The passive inventory: each kind of frame it understands, built by hand, and how sightings fold
/// into one row per device. Nothing here can send - the parser has no channel to send on.
/// </summary>
public class PassiveTests
{
    private static readonly MacAddress Drive = MacAddress.Parse("00:00:BC:11:22:33");
    private static readonly MacAddress Router = MacAddress.Parse("00:0C:29:00:00:FE");

    [Fact]
    public void AnArpRequestNamesItsSender()
    {
        Assert.True(PassiveFrameParser.TryParse(Arp(Drive, "192.168.1.51", "192.168.1.1"), out PassiveSighting? s));

        Assert.Equal(Drive, s!.Mac);
        Assert.Equal(IPAddress.Parse("192.168.1.51"), s.Address);
        Assert.Equal(PassiveSource.Arp, s.Source);
    }

    /// <summary>RFC 5227: sender 0.0.0.0 is a device checking an address is free before taking it.</summary>
    [Fact]
    public void AnArpProbeIsSaidAsAProbeAndCarriesNoAddress()
    {
        Assert.True(PassiveFrameParser.TryParse(Arp(Drive, "0.0.0.0", "192.168.1.51"), out PassiveSighting? s));

        Assert.Null(s!.Address);
        Assert.Contains("probing for 192.168.1.51", s.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadsThroughAVlanTag()
    {
        byte[] plain = Arp(Drive, "192.168.1.51", "192.168.1.1");
        byte[] tagged = [.. plain[..12], 0x81, 0x00, 0x00, 0x0A, .. plain[12..]];

        Assert.True(PassiveFrameParser.TryParse(tagged, out PassiveSighting? s));
        Assert.Equal((ushort?)10, s!.Vlan);
        Assert.Equal(IPAddress.Parse("192.168.1.51"), s.Address);
    }

    [Fact]
    public void ABootpRequestNamesTheClientInsideIt()
    {
        byte[] bootp = Frames.Bytes(Frames.DhcpDiscover);
        byte[] frame = Udp(Drive, IPAddress.Any, IPAddress.Broadcast, 68, 67, bootp);

        Assert.True(PassiveFrameParser.TryParse(frame, out PassiveSighting? s));
        Assert.Equal(PassiveSource.Dhcp, s!.Source);
        Assert.Equal(MacAddress.Parse(Frames.DeviceMac), s.Mac);
        Assert.Contains("DHCP", s.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void LldpGivesTheSystemNamePortAndManagementAddress()
    {
        var tlvs = new List<byte>();
        Tlv(tlvs, 1, [4, .. Drive.ToArray()]);
        Tlv(tlvs, 2, [7, .. "port-001"u8]);
        Tlv(tlvs, 3, [0, 120]);
        Tlv(tlvs, 5, [.. "et200sp-1"u8]);
        Tlv(tlvs, 8, [5, 1, 192, 168, 1, 60, 2, 0, 0, 0, 1, 0]);
        Tlv(tlvs, 0, []);

        byte[] frame = [.. MacAddress.Parse("01:80:C2:00:00:0E").ToArray(), .. Drive.ToArray(), 0x88, 0xCC, .. tlvs];

        Assert.True(PassiveFrameParser.TryParse(frame, out PassiveSighting? s));
        Assert.Equal("et200sp-1", s!.Name);
        Assert.Equal(IPAddress.Parse("192.168.1.60"), s.Address);
        Assert.Equal("port port-001", s.Detail);
    }

    [Fact]
    public void ProfinetCyclicFramesSayTheDeviceIsInDataExchange()
    {
        byte[] frame = [.. MacAddress.Parse("00:0E:8C:00:00:01").ToArray(), .. Drive.ToArray(), 0x88, 0x92, 0x80, 0x01, 0, 0, 0, 0];

        Assert.True(PassiveFrameParser.TryParse(frame, out PassiveSighting? s));
        Assert.Equal(PassiveSource.ProfinetIo, s!.Source);
    }

    [Fact]
    public void IgnoresFramesItDoesNotUnderstandAndRuntFrames()
    {
        Assert.False(PassiveFrameParser.TryParse(new byte[10], out _));

        byte[] ipv6 = [.. Drive.ToArray(), .. Router.ToArray(), 0x86, 0xDD, .. new byte[40]];
        Assert.False(PassiveFrameParser.TryParse(ipv6, out _));
    }

    [Fact]
    public void FoldsSightingsIntoOneRowAndRanksStatedAddressesFirst()
    {
        var inventory = new PassiveInventory(new FakeOuiLookup());
        DateTimeOffset t = DateTimeOffset.UnixEpoch;

        inventory.Observe(new PassiveSighting(Drive, IPAddress.Parse("10.0.0.9"), PassiveSource.IPv4), t);
        inventory.Observe(new PassiveSighting(Drive, IPAddress.Parse("192.168.1.51"), PassiveSource.Arp), t.AddSeconds(1));
        inventory.Observe(new PassiveSighting(Drive, null, PassiveSource.Lldp, "drive-1"), t.AddSeconds(2));

        PassiveDevice device = Assert.Single(inventory.Snapshot());
        Assert.Equal(IPAddress.Parse("192.168.1.51"), device.Addresses[0]);
        Assert.Equal("drive-1", device.NameText);
        Assert.Equal(PassiveSource.IPv4 | PassiveSource.Arp | PassiveSource.Lldp, device.Sources);
        Assert.Equal(3, device.Frames);
        Assert.False(device.LooksLikeRouter);
    }

    [Fact]
    public void ManyTrafficAddressesBehindOneMacIsARouter()
    {
        var inventory = new PassiveInventory();

        for (int i = 1; i <= 6; i++)
        {
            inventory.Observe(new PassiveSighting(Router, IPAddress.Parse($"10.1.{i}.1"), PassiveSource.IPv4), DateTimeOffset.UnixEpoch);
        }

        Assert.True(Assert.Single(inventory.Snapshot()).LooksLikeRouter);
    }

    [Fact]
    public void ADeviceAskingAndNeverAnsweredIsWaitingForAnAddress()
    {
        var inventory = new PassiveInventory();
        inventory.Observe(new PassiveSighting(Drive, null, PassiveSource.Dhcp, Detail: "BOOTP request"), DateTimeOffset.UnixEpoch);

        Assert.True(Assert.Single(inventory.Snapshot()).IsWaitingForAddress);
    }

    private static byte[] Arp(MacAddress sender, string senderIp, string targetIp)
    {
        byte[] frame = new byte[42];
        EthernetFrame.Write(frame, MacAddress.Parse("FF:FF:FF:FF:FF:FF"), sender, EthernetFrame.Arp);
        Span<byte> arp = frame.AsSpan(14);
        BinaryPrimitives.WriteUInt16BigEndian(arp, 1);
        BinaryPrimitives.WriteUInt16BigEndian(arp[2..], 0x0800);
        arp[4] = 6;
        arp[5] = 4;
        BinaryPrimitives.WriteUInt16BigEndian(arp[6..], 1);
        sender.CopyTo(arp[8..]);
        IPAddress.Parse(senderIp).GetAddressBytes().CopyTo(arp[14..]);
        IPAddress.Parse(targetIp).GetAddressBytes().CopyTo(arp[24..]);
        return frame;
    }

    private static byte[] Udp(MacAddress source, IPAddress from, IPAddress to, ushort sourcePort, ushort destinationPort, byte[] data)
    {
        var frame = new byte[14 + 20 + 8 + data.Length];
        EthernetFrame.Write(frame, MacAddress.Parse("FF:FF:FF:FF:FF:FF"), source, EthernetFrame.IPv4);
        Span<byte> ip = frame.AsSpan(14);
        ip[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(ip[2..], (ushort)(20 + 8 + data.Length));
        ip[8] = 64;
        ip[9] = 17;
        from.GetAddressBytes().CopyTo(ip[12..]);
        to.GetAddressBytes().CopyTo(ip[16..]);
        Span<byte> udp = ip[20..];
        BinaryPrimitives.WriteUInt16BigEndian(udp, sourcePort);
        BinaryPrimitives.WriteUInt16BigEndian(udp[2..], destinationPort);
        BinaryPrimitives.WriteUInt16BigEndian(udp[4..], (ushort)(8 + data.Length));
        data.CopyTo(udp[8..]);
        return frame;
    }

    private static void Tlv(List<byte> into, int type, byte[] value)
    {
        ushort header = (ushort)((type << 9) | value.Length);
        into.Add((byte)(header >> 8));
        into.Add((byte)header);
        into.AddRange(value);
    }
}
