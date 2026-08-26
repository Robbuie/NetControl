namespace NetControl.Core.Oui;

/// <summary>
/// One row of the IEEE registry: a prefix of a stated length, and who holds it.
///
/// This is the writer's input and the dump command's output, not something the lookup path
/// touches - a lookup never materialises one of these, it walks the packed bytes directly.
/// </summary>
/// <param name="Prefix">
/// The assignment as a 48-bit value with the host bits already zeroed, byte 0 of the wire format
/// most significant. So MA-L 00-1D-9C is 0x00_00_1D_9C_00_00_00.
/// </param>
/// <param name="PrefixBits">24 for MA-L, 28 for MA-M, 36 for MA-S and the legacy IAB registry.</param>
/// <param name="Organization">The registrant's name, exactly as the registry spells it.</param>
internal readonly record struct OuiAssignment(ulong Prefix, int PrefixBits, string Organization)
{
    /// <summary>The conventional written form of the prefix, e.g. "00:1D:9C" or "70:B3:D5:1F:2".</summary>
    public string PrefixText
    {
        get
        {
            int nibbles = PrefixBits / 4;
            Span<char> buffer = stackalloc char[nibbles + (nibbles / 2)];
            int at = 0;

            for (int i = 0; i < nibbles; i++)
            {
                if (i > 0 && i % 2 == 0)
                {
                    buffer[at++] = ':';
                }

                int shift = 44 - (i * 4);
                buffer[at++] = "0123456789ABCDEF"[(int)((Prefix >> shift) & 0xF)];
            }

            return new string(buffer[..at]);
        }
    }
}
