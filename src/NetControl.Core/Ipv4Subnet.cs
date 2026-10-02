using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Numerics;

namespace NetControl.Core;

/// <summary>
/// An IPv4 address and mask that have already been checked, and the questions worth asking of the
/// pair: how wide is it, where does it start, and is this other address on it.
///
/// <para>The point of the type is that the checking happens once, at <see cref="TryCreate"/>.
/// Subnet arithmetic written inline is written slightly differently every time - a mask nobody
/// verified was contiguous, a byte-wise comparison that stops one byte early - and it is the sort
/// of wrong that produces a plausible answer rather than an exception.</para>
///
/// <para>Deliberately small. It gained members as callers needed them and it should keep doing
/// that: an address-arithmetic helper full of methods nothing calls is a place for a subtle bug to
/// sit unexercised. Notably absent is anything that <em>chooses</em> an address - see
/// <c>DeviceGridViewModel.PlanDevice</c> for why the tool does not do that.</para>
/// </summary>
public readonly record struct Ipv4Subnet
{
    private readonly uint _address;
    private readonly uint _mask;

    private Ipv4Subnet(uint address, uint mask)
    {
        _address = address;
        _mask = mask;
    }

    /// <summary>Bits set in the mask. 24 for 255.255.255.0.</summary>
    public int PrefixLength => BitOperations.PopCount(_mask);

    /// <summary>The first address in the range - the one no device may hold.</summary>
    public IPAddress Network => ToAddress(_address & _mask);

    /// <summary>
    /// A subnet from an address and its mask, or false when either is missing, is not IPv4, or the
    /// mask's set bits are not contiguous.
    /// </summary>
    public static bool TryCreate(IPAddress? address, IPAddress? mask, out Ipv4Subnet subnet)
    {
        subnet = default;

        if (address is null || mask is null
            || !TryToUInt32(address, out uint addressBits)
            || !TryToUInt32(mask, out uint maskBits)
            || !IsContiguous(maskBits))
        {
            return false;
        }

        subnet = new Ipv4Subnet(addressBits, maskBits);
        return true;
    }

    /// <summary>
    /// Whether a mask is a run of ones followed by a run of zeros. A non-contiguous mask is invalid
    /// but does turn up on misconfigured equipment, and every calculation downstream of one is
    /// meaningless rather than merely wrong.
    /// </summary>
    public static bool IsContiguousMask(IPAddress? mask) =>
        mask is not null && TryToUInt32(mask, out uint bits) && IsContiguous(bits);

    /// <summary>
    /// The last address in the range - the directed broadcast, which no device may hold and which
    /// nothing may be sent to by anything calling itself a unicast probe.
    /// </summary>
    public IPAddress Broadcast => ToAddress((_address & _mask) | ~_mask);

    /// <summary>The mask, as an address. Added for the subnet calculator, which has to show it back.</summary>
    public IPAddress Mask => ToAddress(_mask);

    /// <summary>The mask inverted - what ACLs and some switch configurations ask for.</summary>
    public IPAddress Wildcard => ToAddress(~_mask);

    /// <summary>
    /// How many addresses a device may hold. A /32 is one host and a /31 two (RFC 3021 - no network
    /// or broadcast address on a point-to-point link); anything wider loses those two.
    /// </summary>
    public long HostCount => PrefixLength switch
    {
        32 => 1,
        31 => 2,
        _ => (1L << (32 - PrefixLength)) - 2,
    };

    /// <summary>The first address a device may hold.</summary>
    public IPAddress FirstHost => PrefixLength >= 31 ? Network : ToAddress((_address & _mask) + 1);

    /// <summary>The last address a device may hold.</summary>
    public IPAddress LastHost => PrefixLength >= 31 ? Broadcast : ToAddress(((_address & _mask) | ~_mask) - 1);

    /// <summary>The address this subnet was created from - the host, not the network.</summary>
    public IPAddress Address => ToAddress(_address);

    /// <summary>A subnet from an address and a prefix length, 0 to 32.</summary>
    public static bool TryFromPrefix(IPAddress? address, int prefixLength, out Ipv4Subnet subnet)
    {
        subnet = default;

        if (prefixLength is < 0 or > 32 || address is null || !TryToUInt32(address, out uint addressBits))
        {
            return false;
        }

        // A shift by 32 is a shift by 0 in C#, so /0 is spelled out rather than computed.
        uint mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
        subnet = new Ipv4Subnet(addressBits, mask);
        return true;
    }

    /// <summary>Whether <paramref name="address"/> is on this segment, boundaries included.</summary>
    public bool Contains(IPAddress? address) =>
        address is not null
        && TryToUInt32(address, out uint bits)
        && (bits & _mask) == (_address & _mask);

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Network}/{PrefixLength}");

    /// <summary>
    /// Inverting a contiguous mask and adding one carries every bit away, leaving zero. Anything
    /// with a hole in it does not.
    /// </summary>
    private static bool IsContiguous(uint mask)
    {
        uint inverted = ~mask;
        return (inverted & (inverted + 1)) == 0;
    }

    private static bool TryToUInt32(IPAddress? address, out uint value)
    {
        value = 0;

        if (address is null || address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[4];
        if (!address.TryWriteBytes(bytes, out int written) || written != 4)
        {
            return false;
        }

        value = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        return true;
    }

    private static IPAddress ToAddress(uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return new IPAddress(bytes);
    }
}
