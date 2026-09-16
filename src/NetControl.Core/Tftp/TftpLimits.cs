using System.Globalization;

namespace NetControl.Core.Tftp;

/// <summary>
/// The numbers in this protocol that matter, and why each one does.
///
/// <para>They are gathered here rather than spread through the codec because most of them are
/// the direct cause of a field failure, and a reader who wants to know whether a 180 MB image
/// can cross this wire should find the answer in one place.</para>
/// </summary>
public static class TftpLimits
{
    /// <summary>The well-known port a request is sent to. Nothing else in a transfer uses it.</summary>
    public const int ServerPort = 69;

    /// <summary>RFC 1350's block size, and what you get when nobody negotiates anything.</summary>
    public const int DefaultBlockSize = 512;

    /// <summary>RFC 2348 lower bound.</summary>
    public const int MinBlockSize = 8;

    /// <summary>
    /// RFC 2348 upper bound: the largest block that still fits an IPv4 datagram without
    /// fragmenting at the usual 65535-octet ceiling. Sizes above the path MTU are legal and
    /// fragment, which on a plant network is its own way to fail intermittently.
    /// </summary>
    public const int MaxBlockSize = 65464;

    /// <summary>
    /// The largest block that never fragments on ordinary Ethernet: 1500 MTU, less 20 octets of
    /// IP header, less 8 of UDP, less 4 of TFTP header. Worth preferring over the RFC maximum
    /// when choosing a value to ask for.
    /// </summary>
    public const int UnfragmentedBlockSize = 1468;

    public const int MinTimeoutSeconds = 1;

    public const int MaxTimeoutSeconds = 255;

    public const int MinWindowSize = 1;

    public const int MaxWindowSize = 65535;

    /// <summary>
    /// The last block number before the 16-bit counter has nowhere to go.
    /// </summary>
    public const int LastBlockNumber = 65535;

    /// <summary>
    /// 65535 blocks of 512 octets: 33,553,920 bytes, a shade under 32 MiB, and the ceiling on
    /// any transfer where neither end negotiated a larger block.
    ///
    /// <para><b>This is the trap, and it is not the one it looks like.</b> Past this point the
    /// block counter has to wrap, and RFC 1350 does not say to what: implementations disagree
    /// between wrapping to 0 and wrapping to 1, and two ends that disagree do not fail - they
    /// write a file that is the right length and wrong in the middle. A FANUC image runs well
    /// past this ceiling.</para>
    ///
    /// <para>What it is not is a reason a backup never <i>starts</i>. This limit is reached
    /// twenty minutes in. A transfer that dies at the first packet died of something else.</para>
    /// </summary>
    public const long ClassicMaxTransferBytes = (long)LastBlockNumber * DefaultBlockSize;

    /// <summary>
    /// How many octets can cross before the block counter must wrap, at a given block size.
    ///
    /// <para>Negotiating <see cref="UnfragmentedBlockSize"/> raises the ceiling to 96,205,380
    /// bytes - about 91.7 MiB. <b>That is not high enough to make the rollover question go
    /// away.</b> A FANUC image runs from a few megabytes to a few hundred, so an image at the
    /// large end wraps the counter even at the biggest block size that still fits an Ethernet
    /// frame, and any advice that stops at "negotiate blksize" is wrong for exactly the transfers
    /// most likely to be failing. Escaping rollover above ~92 MiB needs a block size that
    /// fragments, which trades one intermittent failure for another.</para>
    /// </summary>
    public static long MaxTransferBytes(int blockSize) =>
        (long)LastBlockNumber * blockSize;

    /// <summary>
    /// The smallest block size at which a transfer of this many octets never wraps the block
    /// counter, or null when no legal block size is big enough.
    ///
    /// <para>The answer is frequently larger than <see cref="UnfragmentedBlockSize"/>, and a
    /// caller showing it should say so: a block size above the path MTU is legal, and it
    /// fragments, and a fragmented TFTP transfer on a plant network fails in its own way.</para>
    /// </summary>
    public static int? SmallestBlockSizeWithoutRollover(long transferBytes)
    {
        if (transferBytes <= 0)
        {
            return MinBlockSize;
        }

        long needed = (transferBytes + LastBlockNumber - 1) / LastBlockNumber;

        if (needed > MaxBlockSize)
        {
            return null;
        }

        return (int)Math.Max(needed, MinBlockSize);
    }

    /// <summary>True when a block size is one RFC 2348 permits asking for.</summary>
    public static bool IsBlockSizeInRange(int blockSize) =>
        blockSize is >= MinBlockSize and <= MaxBlockSize;

    public static bool IsTimeoutInRange(int seconds) =>
        seconds is >= MinTimeoutSeconds and <= MaxTimeoutSeconds;

    public static bool IsWindowSizeInRange(int window) =>
        window is >= MinWindowSize and <= MaxWindowSize;

    /// <summary>
    /// Whether a transfer of this size at this block size would have to wrap the block counter,
    /// and therefore depends on both ends agreeing about something the specification does not
    /// settle.
    /// </summary>
    public static bool NeedsBlockRollover(long transferBytes, int blockSize) =>
        blockSize > 0 && transferBytes > MaxTransferBytes(blockSize);

    /// <summary>One sentence about the rollover risk for a transfer, or null when there is none.</summary>
    public static string? DescribeRolloverRisk(long transferBytes, int blockSize)
    {
        if (!NeedsBlockRollover(transferBytes, blockSize))
        {
            return null;
        }

        string size = transferBytes.ToString("N0", CultureInfo.InvariantCulture);
        string ceiling = MaxTransferBytes(blockSize).ToString("N0", CultureInfo.InvariantCulture);

        string escape = SmallestBlockSizeWithoutRollover(transferBytes) switch
        {
            null =>
                " No block size RFC 2348 allows is large enough to avoid it, so both ends have to agree "
                + "about wrapping.",
            { } needed when needed <= UnfragmentedBlockSize =>
                $" Negotiating blksize={needed} or more avoids it, and still fits an Ethernet frame.",
            { } needed =>
                $" Avoiding it outright would need blksize={needed}, which is above the {UnfragmentedBlockSize}"
                + "-octet Ethernet limit and would fragment - so for a transfer this size, both ends "
                + "agreeing about wrapping matters more than the block size does.",
        };

        return $"{size} bytes at a {blockSize}-octet block size needs the 16-bit block counter to wrap "
            + $"- it only reaches {ceiling} bytes without wrapping. RFC 1350 does not say whether to wrap "
            + "to 0 or to 1, so two ends that disagree produce a file of the right length with the wrong "
            + "contents." + escape;
    }
}
