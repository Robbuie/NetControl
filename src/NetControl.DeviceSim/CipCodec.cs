using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace NetControl.DeviceSim;

/// <summary>
/// Server-side CIP encode/decode. This is the mirror image of the client codec:
/// it parses requests and builds replies.
///
/// Every method here is deliberately non-async. Span&lt;T&gt; locals cannot be declared
/// inside an async method (CS4013), so all packet work stays in plain static helpers
/// that the async socket loops call into.
/// </summary>
public readonly record struct CipPath(ushort Class, ushort Instance, ushort? Attribute)
{
    public override string ToString() =>
        $"class 0x{Class:X2}, instance {Instance}" + (Attribute is { } a ? $", attribute {a}" : "");
}

public static class CipStatus
{
    public const byte Success              = 0x00;
    public const byte PathSegmentError     = 0x04;
    public const byte PathDestinationUnknown = 0x05;
    public const byte ServiceNotSupported  = 0x08;
    public const byte InvalidAttributeValue = 0x09;
    public const byte ObjectStateConflict  = 0x0C;
    public const byte AttributeNotSettable = 0x0E;
    public const byte NotEnoughData        = 0x13;
    public const byte AttributeNotSupported = 0x14;
    public const byte TooMuchData          = 0x15;
    public const byte ObjectDoesNotExist   = 0x16;
}

public static class CipCodec
{
    public const byte GetAttributeAll    = 0x01;
    public const byte SetAttributeAll    = 0x02;
    public const byte Reset              = 0x05;
    public const byte GetAttributeSingle = 0x0E;
    public const byte SetAttributeSingle = 0x10;

    /// <summary>Request layout: service(1), path size in 16-bit words(1), EPATH, data.</summary>
    public static bool TryParseRequest(ReadOnlySpan<byte> b, out byte service, out CipPath path, out byte[] data)
    {
        service = 0;
        path = default;
        data = [];
        if (b.Length < 2) return false;

        service = b[0];
        int pathBytes = b[1] * 2;
        if (b.Length < 2 + pathBytes) return false;

        if (!TryParsePath(b.Slice(2, pathBytes), out path)) return false;
        data = b[(2 + pathBytes)..].ToArray();
        return true;
    }

    private static bool TryParsePath(ReadOnlySpan<byte> p, out CipPath path)
    {
        ushort cls = 0, inst = 0;
        ushort? attr = null;

        int i = 0;
        while (i < p.Length)
        {
            byte segment = p[i++];
            switch (segment)
            {
                // 8-bit logical segments
                case 0x20 when i < p.Length: cls  = p[i++]; break;
                case 0x24 when i < p.Length: inst = p[i++]; break;
                case 0x30 when i < p.Length: attr = p[i++]; break;

                // 16-bit logical segments: one pad byte, then a little-endian UINT
                case 0x21 when i + 2 < p.Length: i++; cls  = BinaryPrimitives.ReadUInt16LittleEndian(p[i..]); i += 2; break;
                case 0x25 when i + 2 < p.Length: i++; inst = BinaryPrimitives.ReadUInt16LittleEndian(p[i..]); i += 2; break;
                case 0x31 when i + 2 < p.Length: i++; attr = BinaryPrimitives.ReadUInt16LittleEndian(p[i..]); i += 2; break;

                case 0x00: break;   // trailing pad
                default:
                    path = default;
                    return false;
            }
        }

        path = new CipPath(cls, inst, attr);
        return true;
    }

    /// <summary>Reply layout: reply service (request | 0x80), reserved 0, status, ext status size, data.</summary>
    public static byte[] BuildReply(byte requestService, byte generalStatus, ReadOnlySpan<byte> data)
    {
        var buf = new byte[4 + data.Length];
        buf[0] = (byte)(requestService | 0x80);
        buf[1] = 0;
        buf[2] = generalStatus;
        buf[3] = 0;                       // no additional status words
        data.CopyTo(buf.AsSpan(4));
        return buf;
    }

    public static byte[] BuildReply(byte requestService, byte generalStatus) =>
        BuildReply(requestService, generalStatus, ReadOnlySpan<byte>.Empty);

    // --- primitive writers -------------------------------------------------

    public static byte[] U32(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        return b;
    }

    public static byte[] U16(ushort v)
    {
        var b = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, v);
        return b;
    }

    public static uint ReadU32(ReadOnlySpan<byte> b) =>
        b.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(b) : 0u;

    /// <summary>
    /// An IPv4 address as a CIP UDINT. CIP carries these little-endian, which is the
    /// reverse of IPAddress.GetAddressBytes() - the single most common bug in this layer.
    /// </summary>
    public static void WriteCipIp(Span<byte> dst, IPAddress ip) =>
        BinaryPrimitives.WriteUInt32LittleEndian(dst, BinaryPrimitives.ReadUInt32BigEndian(ip.GetAddressBytes()));

    public static IPAddress ReadCipIp(ReadOnlySpan<byte> src)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, BinaryPrimitives.ReadUInt32LittleEndian(src));
        return new IPAddress(b);
    }

    /// <summary>CIP STRING: UINT length, then the characters, padded to an even length.</summary>
    public static byte[] CipString(string s)
    {
        var chars = Encoding.ASCII.GetBytes(s);
        var buf = new byte[2 + chars.Length + (chars.Length % 2)];
        BinaryPrimitives.WriteUInt16LittleEndian(buf, (ushort)chars.Length);
        chars.CopyTo(buf.AsSpan(2));
        return buf;
    }

    /// <summary>CIP SHORT_STRING: single length byte, then the characters.</summary>
    public static byte[] CipShortString(string s)
    {
        var chars = Encoding.ASCII.GetBytes(s);
        var buf = new byte[1 + chars.Length];
        buf[0] = (byte)chars.Length;
        chars.CopyTo(buf.AsSpan(1));
        return buf;
    }

    public static byte[] Concat(params byte[][] parts)
    {
        var buf = new byte[parts.Sum(p => p.Length)];
        int i = 0;
        foreach (var p in parts) { p.CopyTo(buf, i); i += p.Length; }
        return buf;
    }
}
