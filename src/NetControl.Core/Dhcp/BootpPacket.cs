using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace NetControl.Core.Dhcp;

/// <summary>
/// An RFC 951 / RFC 2131 BOOTP-DHCP message: a 236-byte fixed header, then the magic cookie
/// 99.130.83.99, then TLV options.
///
/// Everything on this wire is BIG-endian, which is worth stating loudly because the CIP side of
/// this product is little-endian - including the IP addresses carried inside CIP attributes.
/// Mixing the two up is the most expensive protocol mistake available in this codebase, so this
/// type never uses <see cref="BitConverter"/>; every multi-byte read and write names its order
/// at the call site via <see cref="BinaryPrimitives"/>.
///
/// Parsing and serialising are deliberately synchronous and span-based. They are called from the
/// server's async receive loop, and <c>Span&lt;T&gt;</c> locals are illegal in async methods
/// (CS4013), so the codec stays here in plain static helpers.
/// </summary>
public sealed class BootpPacket
{
    /// <summary>op..file inclusive, before the magic cookie.</summary>
    public const int FixedHeaderLength = 236;

    /// <summary>
    /// RFC 1542 section 2.1: a BOOTP message is at least 300 octets. Some older adapters drop
    /// anything shorter, which presents as a device that requests forever and never takes the
    /// address. If a device is ever found that needs more, raise this per-reply rather than
    /// changing it globally - RFC 2131 clients must accept an options field of 312 octets, so
    /// 548 is the other value worth trying.
    /// </summary>
    public const int MinimumMessageLength = 300;

    public const byte OpBootRequest = 1;
    public const byte OpBootReply = 2;

    /// <summary>Ethernet, the only hardware type this tool speaks.</summary>
    public const byte HardwareTypeEthernet = 1;

    private const int MaxChAddrLength = 16;
    private const int SNameLength = 64;
    private const int FileLength = 128;
    private const int SNameOffset = 44;
    private const int FileOffset = 108;

    private static ReadOnlySpan<byte> MagicCookie => [0x63, 0x82, 0x53, 0x63];

    public byte Op { get; set; }

    public byte HardwareType { get; set; } = HardwareTypeEthernet;

    public byte HardwareAddressLength { get; set; } = 6;

    public byte Hops { get; set; }

    /// <summary>Transaction id. Together with the client MAC it identifies a retransmit.</summary>
    public uint TransactionId { get; set; }

    public ushort Seconds { get; set; }

    /// <summary>Bit 15 set means the client is asking for a broadcast reply.</summary>
    public ushort Flags { get; set; }

    public IPAddress ClientIp { get; set; } = IPAddress.Any;

    /// <summary>The address we are handing out. Zero in a request.</summary>
    public IPAddress YourIp { get; set; } = IPAddress.Any;

    public IPAddress ServerIp { get; set; } = IPAddress.Any;

    public IPAddress GatewayIp { get; set; } = IPAddress.Any;

    public MacAddress ClientMac { get; set; }

    public string ServerHostName { get; set; } = string.Empty;

    public string BootFileName { get; set; } = string.Empty;

    public Dictionary<byte, byte[]> Options { get; } = new();

    public bool WantsBroadcastReply => (Flags & 0x8000) != 0;

    public bool IsRequest => Op == OpBootRequest;

    /// <summary>Null means plain BOOTP rather than DHCP, which changes how we must answer.</summary>
    public DhcpMessageType? MessageType =>
        Options.TryGetValue(DhcpOptionCode.DhcpMessageType, out byte[]? v) && v.Length >= 1
            ? (DhcpMessageType)v[0]
            : null;

    public string? HostName => GetAsciiOption(DhcpOptionCode.HostName);

    public string? VendorClass => GetAsciiOption(DhcpOptionCode.VendorClassIdentifier);

    /// <summary>
    /// RFC 2132 option 52. We do not currently read options out of the file and sname fields -
    /// no device met so far uses it - but we record that it happened so a device that does can
    /// be recognised from the log instead of appearing as an inexplicable parse failure.
    /// </summary>
    public bool HasOptionOverload => Options.ContainsKey(DhcpOptionCode.OptionOverload);

    /// <summary>
    /// Parses a received datagram. Returns false only for something that cannot be a BOOTP
    /// message at all; a truncated or malformed options area yields the options parsed so far,
    /// because a device with a slightly wrong options encoding is still a device we want to see
    /// in the log.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> data, out BootpPacket? packet)
    {
        packet = null;
        if (data.Length < FixedHeaderLength)
        {
            return false;
        }

        byte hardwareLength = data[2];
        if (hardwareLength > MaxChAddrLength)
        {
            return false;
        }

        var result = new BootpPacket
        {
            Op = data[0],
            HardwareType = data[1],
            HardwareAddressLength = hardwareLength,
            Hops = data[3],
            TransactionId = BinaryPrimitives.ReadUInt32BigEndian(data[4..]),
            Seconds = BinaryPrimitives.ReadUInt16BigEndian(data[8..]),
            Flags = BinaryPrimitives.ReadUInt16BigEndian(data[10..]),
            ClientIp = ReadAddress(data.Slice(12, 4)),
            YourIp = ReadAddress(data.Slice(16, 4)),
            ServerIp = ReadAddress(data.Slice(20, 4)),
            GatewayIp = ReadAddress(data.Slice(24, 4)),

            // chaddr is a 16-byte field holding an address of hlen bytes. Reading all 16 would
            // pull in padding; trusting hlen blindly would break the devices that report 0.
            ClientMac = hardwareLength >= MacAddress.Length || hardwareLength == 0
                ? new MacAddress(data.Slice(28, MacAddress.Length))
                : MacAddress.Empty,

            ServerHostName = ReadNullTerminatedAscii(data.Slice(SNameOffset, SNameLength)),
            BootFileName = ReadNullTerminatedAscii(data.Slice(FileOffset, FileLength)),
        };

        ReadOnlySpan<byte> rest = data[FixedHeaderLength..];

        // Options are optional in classic BOOTP: no cookie simply means no options.
        if (rest.Length >= MagicCookie.Length && rest[..MagicCookie.Length].SequenceEqual(MagicCookie))
        {
            ParseOptions(rest[MagicCookie.Length..], result.Options);
        }

        packet = result;
        return true;
    }

    /// <summary>
    /// Overload for the receive loop, which holds a reusable buffer and a byte count. Creating
    /// the span here rather than at the call site keeps the async method free of span locals.
    /// </summary>
    public static bool TryParse(byte[] buffer, int length, out BootpPacket? packet)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, buffer.Length);
        return TryParse(buffer.AsSpan(0, length), out packet);
    }

    /// <summary>
    /// Encodes the message, padded to <see cref="MinimumMessageLength"/>.
    /// </summary>
    public byte[] Serialize()
    {
        int optionBytes = 0;
        foreach ((byte code, byte[] value) in Options)
        {
            if (value.Length > byte.MaxValue)
            {
                throw new InvalidOperationException(
                    $"DHCP option {code} is {value.Length} bytes; the TLV length field only holds 255.");
            }

            optionBytes += 2 + value.Length;
        }

        int needed = FixedHeaderLength + MagicCookie.Length + optionBytes + 1;   // +1 for End
        var buffer = new byte[Math.Max(needed, MinimumMessageLength)];
        Span<byte> s = buffer;

        s[0] = Op;
        s[1] = HardwareType;
        s[2] = HardwareAddressLength;
        s[3] = Hops;
        BinaryPrimitives.WriteUInt32BigEndian(s[4..], TransactionId);
        BinaryPrimitives.WriteUInt16BigEndian(s[8..], Seconds);
        BinaryPrimitives.WriteUInt16BigEndian(s[10..], Flags);
        WriteAddress(ClientIp, s.Slice(12, 4));
        WriteAddress(YourIp, s.Slice(16, 4));
        WriteAddress(ServerIp, s.Slice(20, 4));
        WriteAddress(GatewayIp, s.Slice(24, 4));
        ClientMac.CopyTo(s.Slice(28, MacAddress.Length));
        WriteAscii(ServerHostName, s.Slice(SNameOffset, SNameLength));
        WriteAscii(BootFileName, s.Slice(FileOffset, FileLength));

        int i = FixedHeaderLength;
        MagicCookie.CopyTo(s[i..]);
        i += MagicCookie.Length;

        foreach ((byte code, byte[] value) in Options)
        {
            s[i++] = code;
            s[i++] = (byte)value.Length;
            value.CopyTo(s[i..]);
            i += value.Length;
        }

        s[i] = DhcpOptionCode.End;

        // Anything past End is already zero, which is the Pad option. Nothing more to do.
        return buffer;
    }

    public void SetOption(byte code, params byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Options[code] = value;
    }

    public void SetOption(byte code, IPAddress value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Options[code] = value.GetAddressBytes();
    }

    /// <summary>Writes a 32-bit option in network byte order - lease time, renewal, and friends.</summary>
    public void SetOptionUInt32(byte code, uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        Options[code] = bytes;
    }

    public void SetOption(byte code, DhcpMessageType value) => Options[code] = [(byte)value];

    public IPAddress? GetAddressOption(byte code) =>
        Options.TryGetValue(code, out byte[]? v) && v.Length == 4 ? new IPAddress(v) : null;

    public string? GetAsciiOption(byte code) =>
        Options.TryGetValue(code, out byte[]? v) ? Encoding.ASCII.GetString(v).TrimEnd('\0') : null;

    private static void ParseOptions(ReadOnlySpan<byte> options, Dictionary<byte, byte[]> into)
    {
        int i = 0;
        while (i < options.Length)
        {
            byte code = options[i++];

            if (code == DhcpOptionCode.Pad)
            {
                continue;
            }

            if (code == DhcpOptionCode.End)
            {
                break;
            }

            if (i >= options.Length)
            {
                break;      // truncated: length byte missing
            }

            byte length = options[i++];
            if (i + length > options.Length)
            {
                break;      // truncated: value shorter than declared
            }

            into[code] = options.Slice(i, length).ToArray();
            i += length;
        }
    }

    private static IPAddress ReadAddress(ReadOnlySpan<byte> four) => new(four);

    private static void WriteAddress(IPAddress address, Span<byte> destination)
    {
        // IPAddress.GetAddressBytes() is already network order for IPv4, which is what the wire
        // wants here. It is the CIP side that needs the bytes reversed; do not copy this pattern
        // across to NetControl.Core.Cip.
        if (!address.TryWriteBytes(destination, out int written) || written != 4)
        {
            destination.Clear();
        }
    }

    private static string ReadNullTerminatedAscii(ReadOnlySpan<byte> field)
    {
        int end = field.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end < 0 ? field : field[..end]);
    }

    private static void WriteAscii(string value, Span<byte> field)
    {
        field.Clear();
        if (value.Length == 0)
        {
            return;
        }

        // Truncate rather than throw: sname and file are cosmetic here, and a long hostname must
        // not be the reason a panel fails to commission.
        int count = Math.Min(value.Length, field.Length - 1);
        Encoding.ASCII.GetBytes(value.AsSpan(0, count), field);
    }
}
