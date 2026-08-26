using System.Buffers.Binary;
using System.Net;
using System.Text;
using NetControl.Core.Enip;

namespace NetControl.Core.Cip;

/// <summary>
/// TCP/IP Interface Object (0xF5) attribute 5, the Interface Configuration structure: five
/// addresses and a domain name.
///
/// <para><b>The addresses are little-endian UDINTs, which is the reverse of
/// <see cref="IPAddress.GetAddressBytes"/>.</b> This is the single easiest thing in the whole
/// protocol to get wrong, and it fails in the most expensive way: 192.168.1.51 written as
/// 51.1.168.192 is accepted by the device, which then vanishes off the network at an address
/// nobody is looking at. Hence <see cref="ReadAddress"/> and <see cref="WriteAddress"/> rather
/// than a copy anywhere.</para>
/// </summary>
public sealed record InterfaceConfig
{
    /// <summary>Five addresses at four bytes each, then a 2-byte domain length.</summary>
    private const int FixedLength = 22;

    public required IPAddress Ip { get; init; }

    public required IPAddress Mask { get; init; }

    public IPAddress Gateway { get; init; } = IPAddress.Any;

    public IPAddress NameServer1 { get; init; } = IPAddress.Any;

    public IPAddress NameServer2 { get; init; } = IPAddress.Any;

    public string DomainName { get; init; } = string.Empty;

    public static InterfaceConfig Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 20)
        {
            throw new EnipException(
                $"Interface configuration is {bytes.Length} bytes; the five addresses alone are 20.");
        }

        string domain = string.Empty;

        if (bytes.Length >= FixedLength)
        {
            int length = BinaryPrimitives.ReadUInt16LittleEndian(bytes[20..]);
            if (length > 0 && bytes.Length >= FixedLength + length)
            {
                domain = Encoding.ASCII.GetString(bytes.Slice(FixedLength, length));
            }
        }

        return new InterfaceConfig
        {
            Ip = ReadAddress(bytes),
            Mask = ReadAddress(bytes[4..]),
            Gateway = ReadAddress(bytes[8..]),
            NameServer1 = ReadAddress(bytes[12..]),
            NameServer2 = ReadAddress(bytes[16..]),
            DomainName = domain,
        };
    }

    public byte[] Serialize()
    {
        byte[] domain = Encoding.ASCII.GetBytes(DomainName);

        // A CIP STRING is padded to an even overall length.
        int pad = domain.Length % 2;
        var buffer = new byte[FixedLength + domain.Length + pad];

        WriteAddress(buffer, Ip);
        WriteAddress(buffer.AsSpan(4), Mask);
        WriteAddress(buffer.AsSpan(8), Gateway);
        WriteAddress(buffer.AsSpan(12), NameServer1);
        WriteAddress(buffer.AsSpan(16), NameServer2);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(20), (ushort)domain.Length);
        domain.CopyTo(buffer.AsSpan(FixedLength));

        return buffer;
    }

    /// <summary>Whether the device came up as asked. Name servers and domain are not compared -
    /// nothing here writes them, and a device with its own DNS settings is not a failure.</summary>
    public bool Matches(IPAddress ip, IPAddress mask, IPAddress? gateway) =>
        Ip.Equals(ip)
        && Mask.Equals(mask)
        && (gateway is null || Gateway.Equals(gateway));

    public override string ToString() =>
        $"ip={Ip} mask={Mask} gw={Gateway}"
        + (NameServer1.Equals(IPAddress.Any) ? string.Empty : $" dns1={NameServer1}")
        + (NameServer2.Equals(IPAddress.Any) ? string.Empty : $" dns2={NameServer2}")
        + (DomainName.Length == 0 ? string.Empty : $" domain='{DomainName}'");

    /// <summary>
    /// A little-endian UDINT out of a CIP attribute, back into dotted-quad order. Never a memcpy:
    /// see the note on the type.
    /// </summary>
    private static IPAddress ReadAddress(ReadOnlySpan<byte> source)
    {
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(source);
        Span<byte> octets = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(octets, value);
        return new IPAddress(octets);
    }

    private static void WriteAddress(Span<byte> destination, IPAddress address)
    {
        Span<byte> octets = stackalloc byte[4];

        if (!address.TryWriteBytes(octets, out int written) || written != 4)
        {
            throw new EnipException($"{address} is not an IPv4 address, and CIP has nowhere to put anything else.");
        }

        BinaryPrimitives.WriteUInt32LittleEndian(destination, BinaryPrimitives.ReadUInt32BigEndian(octets));
    }
}
