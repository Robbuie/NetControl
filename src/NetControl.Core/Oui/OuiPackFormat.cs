using System.Buffers.Binary;

namespace NetControl.Core.Oui;

/// <summary>
/// The on-disk layout of the packed OUI table, in one place.
///
/// The IEEE registry ships as three CSV files totalling roughly 4 MB of text, which is both too
/// slow to parse at startup and too large to embed as-is. Packing it turns the lookup into a
/// binary search over a byte buffer with no per-entry allocation: the file inflates once, the
/// tables are searched in place, and a vendor name is only turned into a string when someone
/// actually asks for one.
///
/// The reader (<see cref="OuiDatabase"/>), the writer (<see cref="OuiPackWriter"/>) and the
/// round-trip tests all read their constants from here, so the format is described exactly once.
/// If any of the numbers below change, <see cref="FormatVersion"/> must change too - the reader
/// refuses a version it does not know rather than misparsing it into plausible-looking nonsense.
///
/// <code>
/// The file is a Deflate stream. Inflated, it is:
///
///   offset  size  field
///   0       6     magic, ASCII "NCOUI" followed by 0x1A
///   6       2     format version, ushort LE
///   8       8     source date, long LE, DateTime UTC ticks
///   16      4     count of 24-bit (MA-L) records, int LE
///   20      4     count of 28-bit (MA-M) records, int LE
///   24      4     count of 36-bit (MA-S and legacy IAB) records, int LE
///   28      4     length of the name blob in bytes, int LE
///   32      ...   the 24-bit table, then the 28-bit table, then the 36-bit table
///   ...     ...   the name blob
///
/// Each table is Count * 10 bytes, sorted ascending by prefix so it can be binary searched:
///
///   offset  size  field
///   0       6     the prefix, big-endian, as a MAC with the host bits zeroed
///   6       4     byte offset of the organisation name in the name blob, uint LE
///
/// A name blob entry is a ushort LE length followed by that many UTF-8 bytes. Names are shared:
/// a vendor holding forty blocks appears in the blob once and is pointed at forty times, which is
/// most of why the packed file is a fraction of the CSV.
/// </code>
///
/// The prefix is stored big-endian because that makes byte order and numeric order the same, so
/// the sort the writer performs and the comparison the reader performs cannot disagree.
/// </summary>
internal static class OuiPackFormat
{
    /// <summary>ASCII "NCOUI" plus 0x1A - the DOS end-of-file byte, so `type oui.bin` stops here.</summary>
    public static ReadOnlySpan<byte> Magic => "NCOUI\u001A"u8;

    public const int MagicLength = 6;

    /// <summary>
    /// Bumped whenever anything below changes. The reader treats an unknown version as a hard
    /// error: a tool built against a newer pack file should say so, not guess.
    /// </summary>
    public const ushort FormatVersion = 1;

    public const int HeaderLength = 32;

    public const int PrefixLength = 6;
    public const int NameOffsetLength = 4;
    public const int RecordLength = PrefixLength + NameOffsetLength;

    public const int MagicOffset = 0;
    public const int VersionOffset = 6;
    public const int SourceDateOffset = 8;
    public const int Count24Offset = 16;
    public const int Count28Offset = 20;
    public const int Count36Offset = 24;
    public const int NameBlobLengthOffset = 28;

    /// <summary>
    /// The prefix lengths the format stores, longest first - which is also the order a lookup has
    /// to try them in. MA-S and the legacy IAB registry are both 36-bit and share a table; nothing
    /// downstream cares which of the two an assignment came from.
    /// </summary>
    public static ReadOnlySpan<int> PrefixBitsLongestFirst => [36, 28, 24];

    public static bool IsSupportedPrefixBits(int prefixBits) =>
        prefixBits is 24 or 28 or 36;

    /// <summary>
    /// Zeroes everything below <paramref name="prefixBits"/> in a 48-bit address. Applied by the
    /// writer to what the CSV declares and by the reader to the address being looked up, so both
    /// sides derive the stored value the same way rather than agreeing by convention.
    /// </summary>
    public static ulong MaskPrefix(ulong address, int prefixBits) =>
        address & (ulong.MaxValue << (48 - prefixBits)) & 0x0000_FFFF_FFFF_FFFFUL;

    /// <summary>Reads the 6-byte big-endian prefix at the front of a record.</summary>
    public static ulong ReadPrefix(ReadOnlySpan<byte> record)
    {
        ulong value = 0;
        for (int i = 0; i < PrefixLength; i++)
        {
            value = (value << 8) | record[i];
        }

        return value;
    }

    /// <summary>Writes the 6-byte big-endian prefix at the front of a record.</summary>
    public static void WritePrefix(Span<byte> record, ulong prefix)
    {
        for (int i = 0; i < PrefixLength; i++)
        {
            record[i] = (byte)(prefix >> (8 * (PrefixLength - 1 - i)));
        }
    }

    public static uint ReadNameOffset(ReadOnlySpan<byte> record) =>
        BinaryPrimitives.ReadUInt32LittleEndian(record[PrefixLength..]);

    public static void WriteNameOffset(Span<byte> record, uint nameOffset) =>
        BinaryPrimitives.WriteUInt32LittleEndian(record[PrefixLength..], nameOffset);
}
