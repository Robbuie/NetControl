using NetControl.Core.Tftp;
using Xunit;

namespace NetControl.Tests;

/// <summary>
/// The gap between what a client asked for and what a server granted - which every server log
/// omits, and which is where "it works on my bench" lives.
/// </summary>
public class TftpOptionsTests
{
    [Fact]
    public void ReadsTheOptionsThatCarryNumbers()
    {
        var options = new TftpOptions(
        [
            new TftpOption("blksize", "1468"),
            new TftpOption("tsize", "134217728"),
            new TftpOption("timeout", "5"),
            new TftpOption("windowsize", "4"),
        ]);

        Assert.Equal(1468, options.BlockSize);
        Assert.Equal(134217728L, options.TransferSize);
        Assert.Equal(5, options.TimeoutSeconds);
        Assert.Equal(4, options.WindowSize);
    }

    [Fact]
    public void MatchesOptionNamesWithoutRegardToCase()
    {
        var options = new TftpOptions([new TftpOption("BlkSize", "512")]);

        Assert.Equal(512, options.BlockSize);
        Assert.True(options.Has("blksize"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" 512")]
    [InlineData("512 ")]
    [InlineData("+512")]
    [InlineData("-512")]
    [InlineData("1,468")]
    [InlineData("1468bytes")]
    public void RefusesToReadAnOptionValueThatIsNotAPlainNumber(string raw)
    {
        // Null rather than a zero or a guess. A leading sign or a separator means the far end sent
        // something the option is not allowed to contain, and reading it anyway hides that - the
        // same argument that keeps PlanValidation off IPAddress.TryParse.
        var options = new TftpOptions([new TftpOption("blksize", raw)]);

        Assert.True(options.Has("blksize"));
        Assert.Null(options.BlockSize);
    }

    [Fact]
    public void FallsBackToTheRfcDefaultBlockSizeButNeverConfusesItWithAnAnswer()
    {
        // "Asked for the default" and "asked for something we could not read" have to stay apart.
        Assert.Null(TftpOptions.None.BlockSize);
        Assert.Equal(TftpLimits.DefaultBlockSize, TftpOptions.None.EffectiveBlockSize);

        var outOfRange = new TftpOptions([new TftpOption("blksize", "99999")]);
        Assert.Equal(99999, outOfRange.BlockSize);
        Assert.Equal(TftpLimits.DefaultBlockSize, outOfRange.EffectiveBlockSize);
    }

    [Fact]
    public void NamesAnOptionThatWasRequestedAndNeverAcknowledged()
    {
        var requested = new TftpOptions([new TftpOption("blksize", "1468"), new TftpOption("tsize", "0")]);

        IReadOnlyList<string> differences = TftpOptions.DescribeDifferences(requested, TftpOptions.None);

        Assert.Equal(2, differences.Count);
        Assert.Contains(differences, line => line.Contains("blksize=1468", StringComparison.Ordinal)
            && line.Contains("33,553,920", StringComparison.Ordinal));
        Assert.Contains(differences, line => line.Contains("tsize=0", StringComparison.Ordinal));
    }

    [Fact]
    public void NamesAnOptionGrantedAtADifferentValue()
    {
        var requested = new TftpOptions([new TftpOption("blksize", "1468")]);
        var granted = new TftpOptions([new TftpOption("blksize", "512")]);

        IReadOnlyList<string> differences = TftpOptions.DescribeDifferences(requested, granted);

        string line = Assert.Single(differences);
        Assert.Contains("requested as 1468", line, StringComparison.Ordinal);
        Assert.Contains("granted as 512", line, StringComparison.Ordinal);
    }

    [Fact]
    public void NamesAnOptionAcknowledgedThatWasNeverOffered()
    {
        var granted = new TftpOptions([new TftpOption("windowsize", "8")]);

        string line = Assert.Single(TftpOptions.DescribeDifferences(TftpOptions.None, granted));

        Assert.Contains("never requested", line, StringComparison.Ordinal);
    }

    [Fact]
    public void SaysNothingWhenTheTwoEndsAgree()
    {
        var both = new TftpOptions([new TftpOption("blksize", "1468")]);

        Assert.Empty(TftpOptions.DescribeDifferences(both, both));
    }
}
