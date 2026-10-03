using System.Buffers.Binary;
using System.Net;
using System.Text;
using NetControl.Core.Capture;
using NetControl.Core.Dhcp;
using NetControl.Core.Enip;
using NetControl.Core.Profinet;

namespace NetControl.Core.Passive;

/// <summary>
/// Turns one received frame into what it says about its sender. Pure, and never throws on junk:
/// this runs on everything a capture sees, and a malformed frame is ordinary on a plant network.
///
/// <para><b>Listen-only by construction.</b> Nothing in this namespace can transmit - there is no
/// frame channel here, only bytes in and a sighting out - so the passive inventory cannot put a
/// packet on the wire however it is driven.</para>
/// </summary>
public static class PassiveFrameParser
{
    /// <summary>The BPF filter for a passive inventory: everything this parser understands, and nothing else.</summary>
    /// <remarks>
    /// Link-level broadcast and multicast rather than "ip broadcast": the driver has no netmask to
    /// judge an IP broadcast by. <c>vlan</c> is last because in BPF it shifts the offsets of every
    /// test after it.
    /// </remarks>
    public const string Filter =
        "arp or ether proto 0x88cc or ether proto 0x8892 or (udp and (port 67 or port 68 or port 44818 or port 2222)) "
        + "or ether broadcast or ether multicast or vlan";

    private const int UdpHeader = 8;

    public static bool TryParse(ReadOnlySpan<byte> frame, out PassiveSighting? sighting)
    {
        sighting = null;

        if (!EthernetFrame.TryRead(frame, out EthernetFrame ethernet) || ethernet.Source.IsMulticast || ethernet.Source.IsEmpty)
        {
            return false;
        }

        ReadOnlySpan<byte> payload = frame[ethernet.PayloadOffset..];

        sighting = ethernet.EtherType switch
        {
            EthernetFrame.Arp => Arp(payload, ethernet),
            EthernetFrame.IPv4 => IPv4(payload, ethernet),
            EthernetFrame.Lldp => Lldp(payload, ethernet),
            EthernetFrame.Profinet => Profinet(payload, ethernet),
            _ => null,
        };

        return sighting is not null;
    }

    private static PassiveSighting? Arp(ReadOnlySpan<byte> arp, EthernetFrame ethernet)
    {
        // Ethernet/IPv4 ARP only: htype 1, ptype 0800, hlen 6, plen 4.
        if (arp.Length < 28 || BinaryPrimitives.ReadUInt16BigEndian(arp) != 1
            || BinaryPrimitives.ReadUInt16BigEndian(arp[2..]) != 0x0800 || arp[4] != 6 || arp[5] != 4)
        {
            return null;
        }

        var sender = new MacAddress(arp[8..]);
        var senderIp = new IPAddress(arp.Slice(14, 4));
        var targetIp = new IPAddress(arp.Slice(24, 4));

        if (sender.IsEmpty || sender.IsMulticast)
        {
            return null;
        }

        // RFC 5227 address conflict detection: a probe has sender 0.0.0.0 and asks about the address
        // the device is about to take. Worth its own line - a device probing for an address that is
        // already answering is a conflict about to happen.
        if (senderIp.Equals(IPAddress.Any))
        {
            return new PassiveSighting(sender, null, PassiveSource.Arp, Detail: $"probing for {targetIp} before taking it", Vlan: ethernet.Vlan);
        }

        string? detail = senderIp.Equals(targetIp) ? "announced its address (gratuitous ARP)" : null;
        return new PassiveSighting(sender, senderIp, PassiveSource.Arp, Detail: detail, Vlan: ethernet.Vlan);
    }

    private static PassiveSighting? IPv4(ReadOnlySpan<byte> ip, EthernetFrame ethernet)
    {
        if (ip.Length < 20 || (ip[0] >> 4) != 4)
        {
            return null;
        }

        int headerLength = (ip[0] & 0x0F) * 4;
        if (headerLength < 20 || ip.Length < headerLength)
        {
            return null;
        }

        var source = new IPAddress(ip.Slice(12, 4));
        bool firstFragment = (BinaryPrimitives.ReadUInt16BigEndian(ip[6..]) & 0x1FFF) == 0;

        if (ip[9] == 17 && firstFragment && ip.Length >= headerLength + UdpHeader)
        {
            ReadOnlySpan<byte> udp = ip[headerLength..];
            ushort sourcePort = BinaryPrimitives.ReadUInt16BigEndian(udp);
            ushort destinationPort = BinaryPrimitives.ReadUInt16BigEndian(udp[2..]);
            ReadOnlySpan<byte> data = udp[UdpHeader..];

            if (sourcePort == 68 && destinationPort == 67)
            {
                return BootpRequest(data, ethernet);
            }

            if (sourcePort == 44818
                && ListIdentityReply.TryParse(data, new IPEndPoint(source, 44818), out ListIdentityReply? identity)
                && identity is not null)
            {
                return new PassiveSighting(
                    ethernet.Source, source, PassiveSource.EnipIdentity, identity.ProductName,
                    $"serial {identity.SerialText}, rev {identity.Revision}", ethernet.Vlan);
            }

            if (sourcePort == 2222 || destinationPort == 2222)
            {
                return Unicast(source) ? new PassiveSighting(ethernet.Source, source, PassiveSource.EnipIo, Vlan: ethernet.Vlan) : null;
            }
        }

        return Unicast(source) ? new PassiveSighting(ethernet.Source, source, PassiveSource.IPv4, Vlan: ethernet.Vlan) : null;
    }

    private static PassiveSighting? BootpRequest(ReadOnlySpan<byte> data, EthernetFrame ethernet)
    {
        if (!BootpPacket.TryParse(data, out BootpPacket? request) || request is null || !request.IsRequest)
        {
            return null;
        }

        IPAddress? asked = request.GetAddressOption(DhcpOptionCode.RequestedIpAddress)
            ?? (request.ClientIp.Equals(IPAddress.Any) ? null : request.ClientIp);

        string kind = request.MessageType is { } type ? $"DHCP {type}" : "BOOTP request";
        string? vendor = request.VendorClass;

        return new PassiveSighting(
            request.ClientMac.IsEmpty ? ethernet.Source : request.ClientMac,
            asked,
            PassiveSource.Dhcp,
            request.HostName,
            vendor is null ? kind : $"{kind}, vendor class '{vendor}'",
            ethernet.Vlan);
    }

    /// <summary>LLDP TLVs: system name, port, description and management address.</summary>
    private static PassiveSighting? Lldp(ReadOnlySpan<byte> lldp, EthernetFrame ethernet)
    {
        string? name = null, port = null, description = null;
        IPAddress? management = null;
        int at = 0;

        while (at + 2 <= lldp.Length)
        {
            ushort header = BinaryPrimitives.ReadUInt16BigEndian(lldp[at..]);
            int type = header >> 9;
            int length = header & 0x01FF;
            at += 2;

            if (type == 0 || at + length > lldp.Length)
            {
                break;
            }

            ReadOnlySpan<byte> value = lldp.Slice(at, length);

            switch (type)
            {
                case 2 when length > 1:
                    port = Printable(value[1..]);
                    break;
                case 5:
                    name = Printable(value);
                    break;
                case 6:
                    description = Printable(value);
                    break;
                case 8 when length >= 6 && value[1] == 1 && value[0] >= 5:
                    management ??= new IPAddress(value.Slice(2, 4));
                    break;
            }

            at += length;
        }

        string? detail = (port, description) switch
        {
            (null, null) => null,
            (not null, null) => $"port {port}",
            (null, not null) => description,
            _ => $"port {port}; {description}",
        };

        return new PassiveSighting(ethernet.Source, management, PassiveSource.Lldp, name, detail, ethernet.Vlan);
    }

    private static PassiveSighting? Profinet(ReadOnlySpan<byte> payload, EthernetFrame ethernet)
    {
        if (payload.Length < 2)
        {
            return null;
        }

        ushort frameId = BinaryPrimitives.ReadUInt16BigEndian(payload);

        // Cyclic real-time data: the device is in data exchange with a controller right now.
        if (frameId is >= 0x8000 and <= 0xBFFF)
        {
            return new PassiveSighting(ethernet.Source, null, PassiveSource.ProfinetIo, Detail: "exchanging cyclic I/O", Vlan: ethernet.Vlan);
        }

        if (frameId is DcpFrame.FrameIdIdentifyResponse or DcpFrame.FrameIdHello
            && DcpFrame.TryParseDevice(payload, ethernet.Source, out DcpDevice? device) && device is not null)
        {
            return new PassiveSighting(
                ethernet.Source,
                device.Address,
                PassiveSource.ProfinetDcp,
                string.IsNullOrEmpty(device.NameOfStation) ? null : device.NameOfStation,
                device.Describe(),
                ethernet.Vlan);
        }

        return null;
    }

    private static bool Unicast(IPAddress address)
    {
        byte first = address.GetAddressBytes()[0];
        return !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.Broadcast) && first is not (>= 224 and <= 239) and not 0;
    }

    private static string? Printable(ReadOnlySpan<byte> value)
    {
        var text = new StringBuilder(value.Length);
        foreach (byte b in value)
        {
            if (b is >= 0x20 and < 0x7F)
            {
                text.Append((char)b);
            }
        }

        string trimmed = text.ToString().Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}
