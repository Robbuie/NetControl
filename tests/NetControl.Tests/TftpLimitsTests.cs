using NetControl.Core.Tftp;
using Xunit;

namespace NetControl.Tests;

/// <summary>
/// The arithmetic that decides whether a robot image can cross this wire at all.
/// </summary>
public class TftpLimitsTests
{
    [Fact]
    public void ClassicTftpStopsJustShortOf32MiB()
    {
        Assert.Equal(33_553_920L, TftpLimits.ClassicMaxTransferBytes);
        Assert.Equal(TftpLimits.ClassicMaxTransferBytes, TftpLimits.MaxTransferBytes(TftpLimits.DefaultBlockSize));
    }

    [Fact]
    public void NegotiatingTheLargestUnfragmentedBlockDoesNotSaveALargeImage()
    {
        // The correction this module's Python port produced, and the reason it is a test rather
        // than a comment. The first draft of the plan said negotiating blksize was "the fix" for
        // block-counter rollover. At the largest block that still fits an Ethernet frame the
        // ceiling is 96,205,380 bytes - about 91.7 MiB - so an image at the large end of FANUC's
        // range wraps the counter anyway, and advice that stops at "negotiate blksize" is wrong
        // for exactly the transfers most likely to be failing.
        Assert.Equal(96_205_380L, TftpLimits.MaxTransferBytes(TftpLimits.UnfragmentedBlockSize));

        const long sixtyFourMiB = 64L * 1024 * 1024;
        const long oneHundredAndTwentyEightMiB = 128L * 1024 * 1024;

        Assert.True(TftpLimits.NeedsBlockRollover(sixtyFourMiB, TftpLimits.DefaultBlockSize));
        Assert.False(TftpLimits.NeedsBlockRollover(sixtyFourMiB, TftpLimits.UnfragmentedBlockSize));

        Assert.True(TftpLimits.NeedsBlockRollover(oneHundredAndTwentyEightMiB, TftpLimits.DefaultBlockSize));
        Assert.True(TftpLimits.NeedsBlockRollover(oneHundredAndTwentyEightMiB, TftpLimits.UnfragmentedBlockSize));
    }

    [Fact]
    public void SaysWhichBlockSizeWouldAvoidRolloverAndWhetherItFragments()
    {
        // Under about 92 MiB there is an answer that fits an Ethernet frame.
        int? modest = TftpLimits.SmallestBlockSizeWithoutRollover(64L * 1024 * 1024);
        Assert.NotNull(modest);
        Assert.True(modest <= TftpLimits.UnfragmentedBlockSize);

        // Above it the only answer fragments, which is a different failure rather than a fix.
        int? large = TftpLimits.SmallestBlockSizeWithoutRollover(128L * 1024 * 1024);
        Assert.NotNull(large);
        Assert.True(large > TftpLimits.UnfragmentedBlockSize);

        string? risk = TftpLimits.DescribeRolloverRisk(128L * 1024 * 1024, TftpLimits.UnfragmentedBlockSize);
        Assert.NotNull(risk);
        Assert.Contains("fragment", risk, StringComparison.Ordinal);
    }

    [Fact]
    public void SaysNothingAboutRolloverForATransferThatDoesNotNeedIt()
    {
        // Silence is the right answer here: a warning on every small transfer is a warning people
        // learn to skip, and then miss on the one that matters.
        Assert.Null(TftpLimits.DescribeRolloverRisk(1024, TftpLimits.DefaultBlockSize));
        Assert.False(TftpLimits.NeedsBlockRollover(1024, TftpLimits.DefaultBlockSize));
    }

    [Theory]
    [InlineData(7, false)]
    [InlineData(8, true)]
    [InlineData(512, true)]
    [InlineData(1468, true)]
    [InlineData(65464, true)]
    [InlineData(65465, false)]
    public void KnowsWhichBlockSizesRfc2348Allows(int blockSize, bool allowed) =>
        Assert.Equal(allowed, TftpLimits.IsBlockSizeInRange(blockSize));
}
