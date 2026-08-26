using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;

namespace NetControl.Core.Oui;

/// <summary>
/// The IEEE OUI registry, packed and searched in place.
///
/// The whole registry is embedded in this assembly - roughly half a megabyte compressed, inflated
/// once on first use into a single byte array that is then only ever read. Lookups binary search
/// that array; nothing is deserialised into objects, and a vendor name only becomes a string when
/// something asks for it. That matters because the request log calls this on every arriving
/// packet, and a device that has just been power-cycled retransmits hard.
///
/// <b>Longest prefix wins.</b> IEEE hands out three sizes of block - MA-L (24 bits), MA-M (28) and
/// MA-S (36) - and the small ones are carved out of 24-bit ranges that the registry lists as
/// belonging to "IEEE Registration Authority". Searching the 24-bit table first would therefore
/// answer "IEEE Registration Authority" for every small vendor, which is worse than useless on a
/// panel full of gateways and sensors from companies too small to own a full OUI. So the search
/// goes 36, then 28, then 24, and stops at the first hit.
///
/// The table is a point-in-time copy, refreshed by the OuiPacker tool. That is why
/// <c>DeviceRecord.Vendor</c> stores the name resolved at import time rather than looking it up on
/// every read: a project file opened in two years should still say what the commissioning engineer
/// saw, not what the registry says after an acquisition.
/// </summary>
public sealed class OuiDatabase : IOuiLookup
{
    private const string BundledResourceName = "NetControl.Core.Oui.oui.bin";

    private static readonly Lazy<OuiDatabase> LazyBundled =
        new(LoadBundled, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<OuiDatabase> LazyEmpty =
        new(() => new OuiDatabase(EmptyImage()), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The inflated file. Never mutated after construction.</summary>
    private readonly byte[] _data;

    private readonly int _table24Offset;
    private readonly int _table28Offset;
    private readonly int _table36Offset;
    private readonly int _nameBlobOffset;

    private readonly int _count24;
    private readonly int _count28;
    private readonly int _count36;
    private readonly int _nameBlobLength;

    /// <summary>
    /// Vendor names already materialised, keyed on their blob offset. A commissioning run sees the
    /// same dozen manufacturers thousands of times, and the alternative is a fresh string
    /// allocation per arriving packet.
    /// </summary>
    private readonly ConcurrentDictionary<uint, string> _names = new();

    private OuiDatabase(byte[] inflated)
    {
        _data = inflated;

        if (_data.Length < OuiPackFormat.HeaderLength)
        {
            throw new OuiException(
                $"The OUI table is {_data.Length} bytes, too short to contain even a header.")
            {
                Remediation = "Re-run `dotnet run --project tools/NetControl.OuiPacker -- update` and rebuild.",
            };
        }

        var header = _data.AsSpan(0, OuiPackFormat.HeaderLength);

        if (!header[..OuiPackFormat.MagicLength].SequenceEqual(OuiPackFormat.Magic))
        {
            throw new OuiException("The OUI table does not start with the expected signature.")
            {
                Remediation = "The embedded oui.bin is not a packed OUI table. Re-run the OuiPacker tool.",
            };
        }

        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(header[OuiPackFormat.VersionOffset..]);
        if (version != OuiPackFormat.FormatVersion)
        {
            throw new OuiException(
                $"The OUI table is format version {version}; this build reads version {OuiPackFormat.FormatVersion}.")
            {
                Remediation = "Re-pack the registry with the OuiPacker tool from this same commit.",
            };
        }

        long ticks = BinaryPrimitives.ReadInt64LittleEndian(header[OuiPackFormat.SourceDateOffset..]);
        SourceDateUtc = ticks is >= 0 and <= 3_155_378_975_999_999_999
            ? new DateTime(ticks, DateTimeKind.Utc)
            : throw new OuiException("The OUI table carries an unreadable source date.");

        _count24 = ReadCount(header, OuiPackFormat.Count24Offset, "24-bit");
        _count28 = ReadCount(header, OuiPackFormat.Count28Offset, "28-bit");
        _count36 = ReadCount(header, OuiPackFormat.Count36Offset, "36-bit");
        _nameBlobLength = ReadCount(header, OuiPackFormat.NameBlobLengthOffset, "name blob");

        _table24Offset = OuiPackFormat.HeaderLength;
        _table28Offset = _table24Offset + (_count24 * OuiPackFormat.RecordLength);
        _table36Offset = _table28Offset + (_count28 * OuiPackFormat.RecordLength);
        _nameBlobOffset = _table36Offset + (_count36 * OuiPackFormat.RecordLength);

        // Checked here rather than at every read, so the lookup path can slice without bounds
        // arithmetic and a truncated file fails once, loudly, at load.
        long required = (long)_nameBlobOffset + _nameBlobLength;
        if (required > _data.Length)
        {
            throw new OuiException(
                $"The OUI table declares {required} bytes of content but is only {_data.Length} bytes long.")
            {
                Remediation = "The file is truncated. Re-run the OuiPacker tool.",
            };
        }
    }

    /// <summary>
    /// The registry embedded in this build. Inflated on first use and shared from then on.
    /// </summary>
    public static OuiDatabase Bundled => LazyBundled.Value;

    /// <summary>
    /// A valid table with nothing in it. Every lookup returns null. Useful as an injected
    /// <see cref="IOuiLookup"/> when a test wants vendor resolution to be a no-op.
    /// </summary>
    public static OuiDatabase Empty => LazyEmpty.Value;

    /// <summary>
    /// When the registry files this table was packed from were downloaded. Shown in the UI,
    /// because a table that is three years stale explains an awful lot of "unknown" rows.
    /// </summary>
    public DateTime SourceDateUtc { get; }

    /// <summary>Total assignments across all three prefix lengths.</summary>
    public int Count => _count24 + _count28 + _count36;

    /// <summary>Assignments held at each prefix length, for diagnostics and the packer's dump.</summary>
    public int CountOf(int prefixBits) => prefixBits switch
    {
        24 => _count24,
        28 => _count28,
        36 => _count36,
        _ => 0,
    };

    /// <summary>Reads a packed table from a Deflate stream.</summary>
    public static OuiDatabase Load(Stream packed)
    {
        ArgumentNullException.ThrowIfNull(packed);

        var inflated = new MemoryStream();
        try
        {
            using (var deflate = new DeflateStream(packed, CompressionMode.Decompress, leaveOpen: true))
            {
                deflate.CopyTo(inflated);
            }
        }
        catch (InvalidDataException ex)
        {
            throw new OuiException("The OUI table is not a valid Deflate stream.", ex)
            {
                Remediation = "Re-run the OuiPacker tool to regenerate oui.bin.",
            };
        }

        return new OuiDatabase(inflated.ToArray());
    }

    /// <summary>Reads a packed table from disk. For the packer's dump command and for tests.</summary>
    public static OuiDatabase LoadFile(string path)
    {
        using var file = File.OpenRead(path);
        return Load(file);
    }

    /// <inheritdoc/>
    public string? Lookup(MacAddress mac) => Lookup(mac, out _);

    /// <summary>
    /// As <see cref="Lookup(MacAddress)"/>, and also reports how many bits of the address the
    /// match consumed. The UI uses this to distinguish "this vendor owns the whole 24-bit block"
    /// from "this is a 36-bit carve-out", which is the difference between a confident answer and
    /// a plausible one.
    /// </summary>
    public string? Lookup(MacAddress mac, out int prefixBits)
    {
        prefixBits = 0;

        // A locally administered address encodes nothing about who made the adapter - it was set
        // by software. Virtual machines, teamed NICs and some managed switches all emit them, and
        // answering with whatever 24-bit block the bytes happen to collide with would be a
        // fabrication. Multicast and broadcast are not adapter addresses at all.
        if (mac.IsEmpty || mac.IsBroadcast || mac.IsMulticast || mac.IsLocallyAdministered)
        {
            return null;
        }

        ulong address = mac.Value;

        foreach (int bits in OuiPackFormat.PrefixBitsLongestFirst)
        {
            if (TryFind(TableFor(bits), OuiPackFormat.MaskPrefix(address, bits), out uint nameOffset))
            {
                prefixBits = bits;
                return NameAt(nameOffset);
            }
        }

        return null;
    }

    /// <summary>
    /// Every assignment in the table, longest prefix first. Only the packer's dump command uses
    /// this - the lookup path never enumerates.
    /// </summary>
    internal IEnumerable<OuiAssignment> Enumerate() =>
        EnumerateTable(36).Concat(EnumerateTable(28)).Concat(EnumerateTable(24));

    /// <summary>
    /// Deliberately free of <c>Span</c> locals: this is an iterator, and CS4013 forbids a ref
    /// struct local in one for the same reason it forbids one in an async method. The span work
    /// lives in <see cref="AssignmentAt"/>, which is a plain method.
    /// </summary>
    private IEnumerable<OuiAssignment> EnumerateTable(int prefixBits)
    {
        int count = CountOf(prefixBits);
        for (int i = 0; i < count; i++)
        {
            yield return AssignmentAt(prefixBits, i);
        }
    }

    private OuiAssignment AssignmentAt(int prefixBits, int index)
    {
        var record = RecordAt(TableFor(prefixBits), index);
        return new OuiAssignment(
            OuiPackFormat.ReadPrefix(record),
            prefixBits,
            NameAt(OuiPackFormat.ReadNameOffset(record)));
    }

    private static int ReadCount(ReadOnlySpan<byte> header, int offset, string what)
    {
        int value = BinaryPrimitives.ReadInt32LittleEndian(header[offset..]);
        return value >= 0
            ? value
            : throw new OuiException($"The OUI table declares a negative {what} count ({value}).");
    }

    private static OuiDatabase LoadBundled()
    {
        var assembly = typeof(OuiDatabase).Assembly;
        using Stream? resource = assembly.GetManifestResourceStream(BundledResourceName);

        if (resource is null)
        {
            throw new OuiException($"This build has no embedded OUI table ('{BundledResourceName}').")
            {
                Remediation =
                    "src/NetControl.Core/Oui/oui.bin is missing from the build. Re-run "
                    + "`dotnet run --project tools/NetControl.OuiPacker -- update` and rebuild.",
            };
        }

        return Load(resource);
    }

    /// <summary>A header with three empty tables and an empty name blob - the smallest valid file.</summary>
    private static byte[] EmptyImage()
    {
        var image = new byte[OuiPackFormat.HeaderLength];
        var span = image.AsSpan();

        OuiPackFormat.Magic.CopyTo(span);
        BinaryPrimitives.WriteUInt16LittleEndian(span[OuiPackFormat.VersionOffset..], OuiPackFormat.FormatVersion);
        BinaryPrimitives.WriteInt64LittleEndian(span[OuiPackFormat.SourceDateOffset..], DateTime.UnixEpoch.Ticks);

        return image;
    }

    /// <summary>
    /// Binary search for an exact prefix. The table is sorted ascending and the prefixes are
    /// stored big-endian, so numeric order and byte order agree - see <see cref="OuiPackFormat"/>.
    /// </summary>
    private static bool TryFind(ReadOnlySpan<byte> table, ulong prefix, out uint nameOffset)
    {
        int low = 0;
        int high = (table.Length / OuiPackFormat.RecordLength) - 1;

        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            var record = table.Slice(middle * OuiPackFormat.RecordLength, OuiPackFormat.RecordLength);
            ulong candidate = OuiPackFormat.ReadPrefix(record);

            if (candidate == prefix)
            {
                nameOffset = OuiPackFormat.ReadNameOffset(record);
                return true;
            }

            if (candidate < prefix)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        nameOffset = 0;
        return false;
    }

    private static ReadOnlySpan<byte> RecordAt(ReadOnlySpan<byte> table, int index) =>
        table.Slice(index * OuiPackFormat.RecordLength, OuiPackFormat.RecordLength);

    private ReadOnlySpan<byte> TableFor(int prefixBits) => prefixBits switch
    {
        24 => _data.AsSpan(_table24Offset, _count24 * OuiPackFormat.RecordLength),
        28 => _data.AsSpan(_table28Offset, _count28 * OuiPackFormat.RecordLength),
        36 => _data.AsSpan(_table36Offset, _count36 * OuiPackFormat.RecordLength),
        _ => default,
    };

    private string NameAt(uint offset)
    {
        if (_names.TryGetValue(offset, out string? cached))
        {
            return cached;
        }

        var blob = _data.AsSpan(_nameBlobOffset, _nameBlobLength);
        int at = checked((int)offset);

        if (at + sizeof(ushort) > blob.Length)
        {
            throw new OuiException($"The OUI table points at name offset {offset}, past the end of its name blob.");
        }

        int length = BinaryPrimitives.ReadUInt16LittleEndian(blob[at..]);
        if (at + sizeof(ushort) + length > blob.Length)
        {
            throw new OuiException(
                $"The OUI table has a {length}-byte name at offset {offset} that runs past the end.");
        }

        string name = Encoding.UTF8.GetString(blob.Slice(at + sizeof(ushort), length));
        _names[offset] = name;
        return name;
    }
}
