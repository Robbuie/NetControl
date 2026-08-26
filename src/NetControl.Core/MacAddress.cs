using System.Globalization;
using System.Net.NetworkInformation;

namespace NetControl.Core;

/// <summary>
/// A 48-bit IEEE MAC address.
///
/// This exists as a value type rather than a string because MACs are the primary key of the
/// whole product - the plan is keyed on them, the request log is keyed on them, and the
/// retransmit filter is keyed on them. Strings invite "AA:BB:.." vs "aa-bb-.." mismatches that
/// show up as a device silently never matching its plan, which is exactly the class of bug this
/// tool exists to eliminate. Parse once at the edge, compare as an integer everywhere else.
/// </summary>
public readonly record struct MacAddress
{
    /// <summary>Low 48 bits. Byte 0 of the wire format is the most significant of those.</summary>
    private readonly ulong _value;

    public const int Length = 6;

    private MacAddress(ulong value) => _value = value & 0x0000_FFFF_FFFF_FFFFUL;

    public MacAddress(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < Length)
        {
            throw new ArgumentException($"A MAC address needs {Length} bytes, got {bytes.Length}.", nameof(bytes));
        }

        ulong v = 0;
        for (int i = 0; i < Length; i++)
        {
            v = (v << 8) | bytes[i];
        }

        _value = v;
    }

    /// <summary>All-zero address. Used as "not known yet", never served.</summary>
    public static MacAddress Empty => default;

    public bool IsEmpty => _value == 0;

    /// <summary>
    /// Broadcast (FF:FF:FF:FF:FF:FF). A request claiming this as its client hardware address is
    /// malformed and must never be served.
    /// </summary>
    public bool IsBroadcast => _value == 0x0000_FFFF_FFFF_FFFFUL;

    /// <summary>Bit 0 of the first octet - a group/multicast address, never a real adapter.</summary>
    public bool IsMulticast => ((_value >> 40) & 0x01) != 0;

    /// <summary>Bit 1 of the first octet - locally administered, so an OUI lookup is meaningless.</summary>
    public bool IsLocallyAdministered => ((_value >> 40) & 0x02) != 0;

    /// <summary>
    /// The 24-bit OUI, as the integer used to key the vendor table. Only meaningful when
    /// <see cref="IsLocallyAdministered"/> is false.
    /// </summary>
    public uint Oui => (uint)(_value >> 24);

    /// <summary>
    /// The whole 48-bit address as an integer, byte 0 of the wire format most significant.
    ///
    /// Internal because outside this assembly a MAC should stay an opaque value - the moment it
    /// becomes a number, someone increments it. The OUI table needs it because MA-M and MA-S
    /// assignments are 28 and 36 bits long, so <see cref="Oui"/> is not enough to mask against.
    /// </summary>
    internal ulong Value => _value;

    public void CopyTo(Span<byte> destination)
    {
        if (destination.Length < Length)
        {
            throw new ArgumentException($"Need {Length} bytes of space, got {destination.Length}.", nameof(destination));
        }

        for (int i = 0; i < Length; i++)
        {
            destination[i] = (byte)(_value >> (8 * (Length - 1 - i)));
        }
    }

    public byte[] ToArray()
    {
        var bytes = new byte[Length];
        CopyTo(bytes);
        return bytes;
    }

    public static MacAddress FromPhysicalAddress(PhysicalAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        byte[] bytes = address.GetAddressBytes();
        return bytes.Length == Length ? new MacAddress(bytes) : Empty;
    }

    /// <summary>
    /// Accepts colon-, hyphen-, dot- and space-separated forms as well as bare hex, because a
    /// commissioning CSV is typed by a human and a device label is printed by a vendor, and the
    /// two rarely agree on punctuation.
    /// </summary>
    public static bool TryParse(string? text, out MacAddress result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[Length];
        int nibbles = 0;
        byte current = 0;

        foreach (char c in text)
        {
            if (c is ':' or '-' or '.' or ' ' or '\t')
            {
                continue;
            }

            int digit = c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'a' and <= 'f' => c - 'a' + 10,
                >= 'A' and <= 'F' => c - 'A' + 10,
                _ => -1,
            };
            if (digit < 0)
            {
                return false;
            }

            current = (byte)((current << 4) | digit);
            nibbles++;

            if ((nibbles & 1) == 0)
            {
                int index = (nibbles / 2) - 1;
                if (index >= Length)
                {
                    return false;   // too many bytes
                }

                bytes[index] = current;
                current = 0;
            }
        }

        if (nibbles != Length * 2)
        {
            return false;
        }

        result = new MacAddress(bytes);
        return true;
    }

    public static MacAddress Parse(string text) =>
        TryParse(text, out var mac)
            ? mac
            : throw new FormatException($"'{text}' is not a MAC address. Expected six hex bytes, e.g. 00:1D:9C:C7:B0:70.");

    /// <summary>Canonical form: upper-case, colon-separated. This is what goes in the database.</summary>
    public override string ToString()
    {
        Span<char> buffer = stackalloc char[Length * 3 - 1];
        for (int i = 0; i < Length; i++)
        {
            byte b = (byte)(_value >> (8 * (Length - 1 - i)));
            int at = i * 3;
            if (i > 0)
            {
                buffer[at - 1] = ':';
            }

            b.TryFormat(buffer[at..], out _, "X2", CultureInfo.InvariantCulture);
        }

        return new string(buffer);
    }
}
