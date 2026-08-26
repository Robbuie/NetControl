using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace NetControl.Core.Oui;

/// <summary>
/// Builds the packed table that <see cref="OuiDatabase"/> reads.
///
/// This lives in the engine rather than in the packer tool on purpose. The format is only safe
/// while exactly one piece of code writes it and exactly one reads it; putting the writer beside
/// the reader means a change to <see cref="OuiPackFormat"/> breaks both at compile time, and lets
/// the tests round-trip a table built in memory instead of trusting a checked-in binary. The
/// packer tool is then only a CSV parser and a download, which is all a refresh chore should be.
/// </summary>
internal static class OuiPackWriter
{
    /// <summary>
    /// Writes <paramref name="assignments"/> to <paramref name="destination"/> as a Deflate stream.
    /// </summary>
    /// <param name="destination">Left open; the caller owns it.</param>
    /// <param name="sourceDateUtc">When the registry files were downloaded.</param>
    /// <param name="assignments">
    /// Any order. Duplicates of the same prefix at the same length are collapsed, last one
    /// winning - the IEEE files should not contain any, but a hand-maintained seed file might,
    /// and silently packing both would leave the binary search able to find either.
    /// </param>
    /// <returns>How many assignments were actually written, after collapsing duplicates.</returns>
    public static int Write(Stream destination, DateTime sourceDateUtc, IEnumerable<OuiAssignment> assignments)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(assignments);

        var byLength = new Dictionary<int, SortedDictionary<ulong, string>>
        {
            [24] = new(),
            [28] = new(),
            [36] = new(),
        };

        foreach (var assignment in assignments)
        {
            if (!OuiPackFormat.IsSupportedPrefixBits(assignment.PrefixBits))
            {
                throw new ArgumentException(
                    $"Prefix length {assignment.PrefixBits} is not one of 24, 28 or 36.",
                    nameof(assignments));
            }

            if (string.IsNullOrWhiteSpace(assignment.Organization))
            {
                continue;   // an unnamed registrant tells the user nothing; drop it rather than store ""
            }

            // Mask again here rather than trusting the caller: the reader masks the address it is
            // given, so a prefix stored with stray low bits could never be found.
            ulong prefix = OuiPackFormat.MaskPrefix(assignment.Prefix, assignment.PrefixBits);
            byLength[assignment.PrefixBits][prefix] = assignment.Organization.Trim();
        }

        // One entry per distinct name. A vendor holding forty blocks is stored once and pointed at
        // forty times, and this dedupe is most of the difference between the CSV and the pack.
        var nameOffsets = new Dictionary<string, uint>(StringComparer.Ordinal);
        var nameBlob = new MemoryStream();
        var lengthPrefix = new byte[sizeof(ushort)];

        uint OffsetOf(string name)
        {
            if (nameOffsets.TryGetValue(name, out uint existing))
            {
                return existing;
            }

            byte[] utf8 = Encoding.UTF8.GetBytes(name);
            if (utf8.Length > ushort.MaxValue)
            {
                throw new ArgumentException(
                    $"Organisation name is {utf8.Length} bytes, too long to pack.",
                    nameof(assignments));
            }

            uint offset = checked((uint)nameBlob.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(lengthPrefix, (ushort)utf8.Length);
            nameBlob.Write(lengthPrefix);
            nameBlob.Write(utf8);

            nameOffsets[name] = offset;
            return offset;
        }

        var tables = new Dictionary<int, byte[]>();
        int total = 0;

        int[] ascendingLengths = [24, 28, 36];

        foreach (int bits in ascendingLengths)
        {
            var entries = byLength[bits];
            var table = new byte[entries.Count * OuiPackFormat.RecordLength];
            int at = 0;

            // SortedDictionary already yields ascending prefixes, which is the order the reader's
            // binary search requires.
            foreach (var (prefix, organization) in entries)
            {
                var record = table.AsSpan(at, OuiPackFormat.RecordLength);
                OuiPackFormat.WritePrefix(record, prefix);
                OuiPackFormat.WriteNameOffset(record, OffsetOf(organization));
                at += OuiPackFormat.RecordLength;
            }

            tables[bits] = table;
            total += entries.Count;
        }

        byte[] names = nameBlob.ToArray();
        var header = new byte[OuiPackFormat.HeaderLength];
        var headerSpan = header.AsSpan();

        OuiPackFormat.Magic.CopyTo(headerSpan);
        BinaryPrimitives.WriteUInt16LittleEndian(
            headerSpan[OuiPackFormat.VersionOffset..],
            OuiPackFormat.FormatVersion);
        BinaryPrimitives.WriteInt64LittleEndian(
            headerSpan[OuiPackFormat.SourceDateOffset..],
            sourceDateUtc.ToUniversalTime().Ticks);
        BinaryPrimitives.WriteInt32LittleEndian(headerSpan[OuiPackFormat.Count24Offset..], byLength[24].Count);
        BinaryPrimitives.WriteInt32LittleEndian(headerSpan[OuiPackFormat.Count28Offset..], byLength[28].Count);
        BinaryPrimitives.WriteInt32LittleEndian(headerSpan[OuiPackFormat.Count36Offset..], byLength[36].Count);
        BinaryPrimitives.WriteInt32LittleEndian(headerSpan[OuiPackFormat.NameBlobLengthOffset..], names.Length);

        // SmallestSize because this runs once per registry refresh and the output is committed:
        // a few seconds of packing buys every clone a smaller file forever.
        using (var deflate = new DeflateStream(destination, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            deflate.Write(header);
            deflate.Write(tables[24]);
            deflate.Write(tables[28]);
            deflate.Write(tables[36]);
            deflate.Write(names);
        }

        return total;
    }
}
