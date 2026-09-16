using System.Globalization;

namespace NetControl.Core.Tftp;

/// <summary>
/// The RFC 2347 option set carried by a request or an option acknowledgement.
///
/// <para>The point of this type is the gap it makes visible. A client asks for a block size and
/// a timeout; a server grants some, silently drops others, and answers with a plain ACK when it
/// wants none of them. Every server log records what it decided and none of them records what it
/// was asked, so the difference - which is where "it works on my bench" lives - is invisible in
/// exactly the situation somebody needs it. Requested and granted are two of these, kept apart,
/// and <see cref="DescribeDifferences"/> words the gap between them.</para>
/// </summary>
public sealed record TftpOptions(IReadOnlyList<TftpOption> All)
{
    /// <summary>No options at all - a plain RFC 1350 request, or an ACK where an OACK might have been.</summary>
    public static readonly TftpOptions None = new([]);

    public bool IsEmpty => All.Count == 0;

    public int Count => All.Count;

    /// <summary>The raw string for an option, or null when it was not present.</summary>
    public string? RawValue(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        foreach (TftpOption option in All)
        {
            if (option.Is(name))
            {
                return option.Value;
            }
        }

        return null;
    }

    public bool Has(string name) => RawValue(name) is not null;

    /// <summary>
    /// RFC 2348 block size, or null when it was absent or not a plain number. Null does not mean
    /// 512: the caller needs to be able to tell "asked for the default" from "asked for
    /// something we could not read".
    /// </summary>
    public int? BlockSize => ReadInt32(TftpOption.BlockSizeName);

    public int? TimeoutSeconds => ReadInt32(TftpOption.TimeoutName);

    public long? TransferSize => ReadInt64(TftpOption.TransferSizeName);

    public int? WindowSize => ReadInt32(TftpOption.WindowSizeName);

    /// <summary>The block size that will actually be used: what was granted, else RFC 1350's 512.</summary>
    public int EffectiveBlockSize => BlockSize is { } size && TftpLimits.IsBlockSizeInRange(size)
        ? size
        : TftpLimits.DefaultBlockSize;

    /// <summary>
    /// Reads an unsigned decimal option. <see cref="NumberStyles.None"/> on purpose: a leading
    /// sign, a thousands separator or surrounding whitespace all mean the far end sent something
    /// this option is not allowed to contain, and reading it anyway would hide that.
    /// </summary>
    private int? ReadInt32(string name) =>
        RawValue(name) is { } raw
            && int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int value)
            ? value
            : null;

    private long? ReadInt64(string name) =>
        RawValue(name) is { } raw
            && long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out long value)
            ? value
            : null;

    /// <summary>
    /// Every way the granted set differs from the requested one, in sentences.
    ///
    /// <para>An option that was asked for and not answered is the interesting case and is worded
    /// as such: the transfer proceeds, at the default, and the client that wanted a 1468-octet
    /// block is now sending 512-octet ones and will hit the rollover ceiling six times sooner.
    /// An empty list means the two agree.</para>
    /// </summary>
    public static IReadOnlyList<string> DescribeDifferences(TftpOptions requested, TftpOptions granted)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(granted);

        var differences = new List<string>();

        foreach (TftpOption option in requested.All)
        {
            string? answer = granted.RawValue(option.Name);

            if (answer is null)
            {
                differences.Add(
                    $"{option.Name}={option.Value} was requested and not acknowledged - the transfer runs "
                    + $"without it{DefaultNote(option.Name)}.");
            }
            else if (!string.Equals(answer, option.Value, StringComparison.OrdinalIgnoreCase))
            {
                differences.Add($"{option.Name} was requested as {option.Value} and granted as {answer}.");
            }
        }

        foreach (TftpOption option in granted.All)
        {
            if (requested.RawValue(option.Name) is null)
            {
                differences.Add(
                    $"{option.Name}={option.Value} was acknowledged but never requested - the far end is "
                    + "answering about an option that was not offered.");
            }
        }

        return differences;
    }

    private static string DefaultNote(string name) => name.ToLowerInvariant() switch
    {
        TftpOption.BlockSizeName =>
            $", at RFC 1350's {TftpLimits.DefaultBlockSize}-octet blocks, which caps it at "
            + $"{TftpLimits.ClassicMaxTransferBytes.ToString("N0", CultureInfo.InvariantCulture)} bytes",
        TftpOption.TransferSizeName => ", so neither end knows the size in advance",
        _ => string.Empty,
    };

    public override string ToString() =>
        IsEmpty ? "(none)" : string.Join(", ", All.Select(option => option.ToString()));
}
