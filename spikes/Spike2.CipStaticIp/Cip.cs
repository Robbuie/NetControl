using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace NetControl.Spike.CipStaticIp;

public static class CipService
{
    public const byte GetAttributeAll    = 0x01;
    public const byte SetAttributeAll    = 0x02;
    public const byte Reset              = 0x05;
    public const byte GetAttributeSingle = 0x0E;
    public const byte SetAttributeSingle = 0x10;
}

public static class CipClass
{
    public const ushort Identity         = 0x01;
    public const ushort TcpIpInterface   = 0xF5;   // where the static IP lives
    public const ushort EthernetLink     = 0xF6;   // where the MAC lives
}

/// <summary>A UCMM request: service + EPATH + data.</summary>
public sealed class CipRequest
{
    public byte Service;
    public ushort Class;
    public ushort Instance = 1;
    public ushort? Attribute;
    public byte[] Data = [];

    public static CipRequest GetAttr(ushort cls, ushort instance, ushort attr) =>
        new() { Service = CipService.GetAttributeSingle, Class = cls, Instance = instance, Attribute = attr };

    public static CipRequest SetAttr(ushort cls, ushort instance, ushort attr, byte[] data) =>
        new() { Service = CipService.SetAttributeSingle, Class = cls, Instance = instance, Attribute = attr, Data = data };

    public byte[] Serialize()
    {
        var path = new List<byte>(6);
        AppendSegment(path, 0x20, 0x21, Class);        // class segment
        AppendSegment(path, 0x24, 0x25, Instance);     // instance segment
        if (Attribute is { } a) AppendSegment(path, 0x30, 0x31, a);   // attribute segment

        if (path.Count % 2 != 0) path.Add(0);          // EPATH is measured in 16-bit words

        var buf = new byte[2 + path.Count + Data.Length];
        buf[0] = Service;
        buf[1] = (byte)(path.Count / 2);
        path.CopyTo(buf, 2);
        Data.CopyTo(buf, 2 + path.Count);
        return buf;
    }

    private static void AppendSegment(List<byte> path, byte byteForm, byte wordForm, ushort value)
    {
        if (value <= 0xFF) { path.Add(byteForm); path.Add((byte)value); }
        else
        {
            path.Add(wordForm);
            path.Add(0);                                // pad required before a 16-bit value
            path.Add((byte)(value & 0xFF));
            path.Add((byte)(value >> 8));
        }
    }
}

public sealed class CipResponse
{
    public byte ReplyService;
    public byte GeneralStatus;
    public ushort[] AdditionalStatus = [];
    public byte[] Data = [];

    public bool IsSuccess => GeneralStatus == 0;

    public static CipResponse Parse(ReadOnlySpan<byte> b)
    {
        if (b.Length < 4) throw new EnipException("CIP reply shorter than 4 bytes");
        var r = new CipResponse
        {
            ReplyService = b[0],
            GeneralStatus = b[2],
        };
        int extWords = b[3];
        if (b.Length < 4 + extWords * 2) throw new EnipException("CIP reply truncated in additional status");

        r.AdditionalStatus = new ushort[extWords];
        for (int i = 0; i < extWords; i++)
            r.AdditionalStatus[i] = BinaryPrimitives.ReadUInt16LittleEndian(b[(4 + i * 2)..]);

        r.Data = b[(4 + extWords * 2)..].ToArray();
        return r;
    }

    public string StatusText()
    {
        string s = GeneralStatus switch
        {
            0x00 => "Success",
            0x02 => "Resource unavailable",
            0x04 => "Path segment error - the device does not have that class/instance",
            0x05 => "Path destination unknown",
            0x08 => "Service not supported",
            0x09 => "Invalid attribute value",
            0x0B => "Already in requested mode/state",
            0x0C => "Object state conflict - often means the device must be idle first",
            0x0E => "Attribute not settable - the device is refusing the write",
            0x0F => "Privilege violation",
            0x10 => "Device state conflict",
            0x13 => "Not enough data",
            0x14 => "Attribute not supported",
            0x15 => "Too much data",
            0x1E => "Embedded service error",
            _ => $"General status 0x{GeneralStatus:X2}"
        };
        if (AdditionalStatus.Length > 0)
            s += " [ext " + string.Join(",", AdditionalStatus.Select(x => $"0x{x:X4}")) + "]";
        return s;
    }
}

/// <summary>
/// TCP/IP Interface Object, class 0xF5, attribute 5 - the Interface Configuration
/// structure. All five addresses are UDINTs in LITTLE-endian byte order, which is
/// reversed relative to IPAddress.GetAddressBytes().
/// </summary>
public sealed record InterfaceConfig(
    IPAddress Ip, IPAddress Mask, IPAddress Gateway,
    IPAddress NameServer1, IPAddress NameServer2, string DomainName)
{
    public static InterfaceConfig Parse(ReadOnlySpan<byte> d)
    {
        if (d.Length < 20) throw new EnipException($"Interface configuration too short ({d.Length} bytes)");
        var ip   = ReadIp(d);
        var mask = ReadIp(d[4..]);
        var gw   = ReadIp(d[8..]);
        var ns1  = ReadIp(d[12..]);
        var ns2  = ReadIp(d[16..]);

        string domain = "";
        if (d.Length >= 22)
        {
            ushort len = BinaryPrimitives.ReadUInt16LittleEndian(d[20..]);
            if (len > 0 && d.Length >= 22 + len)
                domain = Encoding.ASCII.GetString(d.Slice(22, len));
        }
        return new InterfaceConfig(ip, mask, gw, ns1, ns2, domain);
    }

    public byte[] Serialize()
    {
        var domain = Encoding.ASCII.GetBytes(DomainName);
        int pad = domain.Length % 2;                     // STRING is padded to an even length
        var buf = new byte[20 + 2 + domain.Length + pad];
        var s = buf.AsSpan();
        WriteIp(s, Ip);
        WriteIp(s[4..], Mask);
        WriteIp(s[8..], Gateway);
        WriteIp(s[12..], NameServer1);
        WriteIp(s[16..], NameServer2);
        BinaryPrimitives.WriteUInt16LittleEndian(s[20..], (ushort)domain.Length);
        domain.CopyTo(s[22..]);
        return buf;
    }

    private static IPAddress ReadIp(ReadOnlySpan<byte> s)
    {
        uint v = BinaryPrimitives.ReadUInt32LittleEndian(s);
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        return new IPAddress(b);
    }

    private static void WriteIp(Span<byte> s, IPAddress ip)
    {
        uint v = BinaryPrimitives.ReadUInt32BigEndian(ip.GetAddressBytes());
        BinaryPrimitives.WriteUInt32LittleEndian(s, v);
    }

    public override string ToString() =>
        $"ip={Ip}  mask={Mask}  gw={Gateway}" +
        (NameServer1.Equals(IPAddress.Any) ? "" : $"  dns1={NameServer1}") +
        (NameServer2.Equals(IPAddress.Any) ? "" : $"  dns2={NameServer2}") +
        (string.IsNullOrEmpty(DomainName) ? "" : $"  domain='{DomainName}'");
}

/// <summary>Class 0xF5, attribute 3 - Configuration Control.</summary>
public enum ConfigMethod : uint { Static = 0, Bootp = 1, Dhcp = 2 }

/// <summary>Class 0xF5, attribute 2 - Configuration Capability bit flags.</summary>
[Flags]
public enum ConfigCapability : uint
{
    BootpClient        = 1 << 0,
    DnsClient          = 1 << 1,
    DhcpClient         = 1 << 2,
    DhcpDnsUpdate      = 1 << 3,
    ConfigurationSettable = 1 << 4,
    HardwareConfigurable  = 1 << 5,
    InterfaceResetNeeded  = 1 << 6,
    AcdCapable            = 1 << 7,
}
