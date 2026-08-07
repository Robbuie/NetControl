using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace NetControl.Spike.BootpListen;

/// <summary>
/// RFC 951 / RFC 2131 BOOTP-DHCP message. Fixed header is 236 bytes, followed by
/// the magic cookie (99.130.83.99) and TLV options.
/// </summary>
public sealed class BootpPacket
{
    public const int FixedHeaderLength = 236;
    private static ReadOnlySpan<byte> MagicCookie => [0x63, 0x82, 0x53, 0x63];

    public byte Op;                    // 1 = BOOTREQUEST, 2 = BOOTREPLY
    public byte HType = 1;             // 1 = Ethernet
    public byte HLen = 6;
    public byte Hops;
    public uint Xid;                   // big-endian on the wire
    public ushort Secs;
    public ushort Flags;               // 0x8000 = client wants a broadcast reply
    public IPAddress CiAddr = IPAddress.Any;
    public IPAddress YiAddr = IPAddress.Any;
    public IPAddress SiAddr = IPAddress.Any;
    public IPAddress GiAddr = IPAddress.Any;
    public byte[] ChAddr = new byte[16];
    public string SName = "";
    public string File = "";
    public Dictionary<byte, byte[]> Options = new();

    public bool WantsBroadcast => (Flags & 0x8000) != 0;

    /// <summary>Client hardware address trimmed to HLen, formatted AA:BB:CC:DD:EE:FF.</summary>
    public string MacString => string.Join(':', ChAddr.Take(Math.Max(HLen, (byte)6)).Select(b => b.ToString("X2")));

    public byte[] Mac => ChAddr.Take(Math.Max(HLen, (byte)6)).ToArray();

    /// <summary>DHCP option 53. Null means this is plain BOOTP, not DHCP.</summary>
    public DhcpMessageType? MessageType =>
        Options.TryGetValue(53, out var v) && v.Length >= 1 ? (DhcpMessageType)v[0] : null;

    public string? HostName =>
        Options.TryGetValue(12, out var v) ? Encoding.ASCII.GetString(v).TrimEnd('\0') : null;

    public string? VendorClass =>
        Options.TryGetValue(60, out var v) ? Encoding.ASCII.GetString(v).TrimEnd('\0') : null;

    public static bool TryParse(ReadOnlySpan<byte> data, out BootpPacket packet)
    {
        packet = new BootpPacket();
        if (data.Length < FixedHeaderLength) return false;

        packet.Op = data[0];
        packet.HType = data[1];
        packet.HLen = data[2];
        packet.Hops = data[3];
        packet.Xid = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
        packet.Secs = BinaryPrimitives.ReadUInt16BigEndian(data[8..]);
        packet.Flags = BinaryPrimitives.ReadUInt16BigEndian(data[10..]);
        packet.CiAddr = new IPAddress(data.Slice(12, 4));
        packet.YiAddr = new IPAddress(data.Slice(16, 4));
        packet.SiAddr = new IPAddress(data.Slice(20, 4));
        packet.GiAddr = new IPAddress(data.Slice(24, 4));
        data.Slice(28, 16).CopyTo(packet.ChAddr);
        packet.SName = Encoding.ASCII.GetString(data.Slice(44, 64)).TrimEnd('\0');
        packet.File = Encoding.ASCII.GetString(data.Slice(108, 128)).TrimEnd('\0');

        // Options are optional in classic BOOTP.
        var rest = data[FixedHeaderLength..];
        if (rest.Length < 4 || !rest[..4].SequenceEqual(MagicCookie)) return true;

        int i = 4;
        while (i < rest.Length)
        {
            byte code = rest[i++];
            if (code == 0) continue;         // pad
            if (code == 255) break;          // end
            if (i >= rest.Length) break;
            byte len = rest[i++];
            if (i + len > rest.Length) break;
            packet.Options[code] = rest.Slice(i, len).ToArray();
            i += len;
        }
        return true;
    }

    /// <summary>
    /// Builds a BOOTREPLY. Total length is padded to 300 bytes of options space, which is
    /// what RFC 1542 requires of a BOOTP server and what older adapters expect.
    /// </summary>
    public byte[] Serialize()
    {
        var buf = new byte[FixedHeaderLength + 312];
        var s = buf.AsSpan();

        s[0] = Op;
        s[1] = HType;
        s[2] = HLen;
        s[3] = Hops;
        BinaryPrimitives.WriteUInt32BigEndian(s[4..], Xid);
        BinaryPrimitives.WriteUInt16BigEndian(s[8..], Secs);
        BinaryPrimitives.WriteUInt16BigEndian(s[10..], Flags);
        CiAddr.GetAddressBytes().CopyTo(s[12..]);
        YiAddr.GetAddressBytes().CopyTo(s[16..]);
        SiAddr.GetAddressBytes().CopyTo(s[20..]);
        GiAddr.GetAddressBytes().CopyTo(s[24..]);
        ChAddr.CopyTo(s[28..]);
        Encoding.ASCII.GetBytes(SName).CopyTo(s[44..]);
        Encoding.ASCII.GetBytes(File).CopyTo(s[108..]);

        int i = FixedHeaderLength;
        MagicCookie.CopyTo(s[i..]); i += 4;

        foreach (var (code, value) in Options)
        {
            s[i++] = code;
            s[i++] = (byte)value.Length;
            value.CopyTo(s[i..]);
            i += value.Length;
        }
        s[i++] = 255;   // end

        // Pad to the 300-byte minimum options area; some devices drop short replies.
        int minimum = FixedHeaderLength + 4 + 296;
        return buf[..Math.Max(i, minimum)];
    }

    public void SetOption(byte code, params byte[] value) => Options[code] = value;
    public void SetOption(byte code, IPAddress value) => Options[code] = value.GetAddressBytes();
    public void SetOptionUInt32(byte code, uint value)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, value);
        Options[code] = b;
    }
}

public enum DhcpMessageType : byte
{
    Discover = 1, Offer = 2, Request = 3, Decline = 4,
    Ack = 5, Nak = 6, Release = 7, Inform = 8
}
