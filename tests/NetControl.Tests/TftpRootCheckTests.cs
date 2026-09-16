using NetControl.Core.Tftp;
using Xunit;

namespace NetControl.Tests;

/// <summary>
/// The dullest third of the failure space, which is also the most common: a root folder that is
/// missing, unwritable, or full.
/// </summary>
public class TftpRootCheckTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ReportsNoFolderAsUnknownRatherThanFine(string? path)
    {
        TftpRootStatus status = TftpRootCheck.Inspect(path);

        // Grey is not green. The same rule the interface bar already holds for the firewall: a
        // check that did not run must never read as good news.
        Assert.Equal(TftpRootVerdict.Unknown, status.Verdict);
        Assert.False(status.IsClear);
    }

    [Fact]
    public void NamesAMissingFolderAndSaysTftpCannotCreateOne()
    {
        string missing = Path.Combine(Path.GetTempPath(), "netcontrol-tests-" + Guid.NewGuid().ToString("N"));

        TftpRootStatus status = TftpRootCheck.Inspect(missing);

        Assert.Equal(TftpRootVerdict.Missing, status.Verdict);
        Assert.False(status.IsClear);
        Assert.False(status.Exists);
        Assert.Contains("cannot create directories", status.Summary, StringComparison.Ordinal);
        Assert.NotNull(status.Remediation);
    }

    [Fact]
    public void GradesAWritableFolderAsWritableAndLeavesNothingBehind()
    {
        string folder = Path.Combine(Path.GetTempPath(), "netcontrol-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        try
        {
            TftpRootStatus status = TftpRootCheck.Inspect(folder);

            Assert.Equal(TftpRootVerdict.Writable, status.Verdict);
            Assert.True(status.IsClear);
            Assert.True(status.Exists);
            Assert.Null(status.Remediation);

            // The probe writes a real file, because every permission API on Windows answers about
            // the ACL rather than about whether a file lands. It has to clean up after itself:
            // this folder is somebody's backup directory in the field.
            Assert.Empty(Directory.GetFiles(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void WarnsRatherThanBlockingWhenSpaceIsShort()
    {
        string folder = Path.Combine(Path.GetTempPath(), "netcontrol-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        try
        {
            // A threshold larger than any real volume, to reach the low-space branch without
            // needing a full disk.
            TftpRootStatus status = TftpRootCheck.Inspect(folder, minimumFreeBytes: long.MaxValue);

            // Free space is unavailable on some paths - a UNC share is the usual case - and a
            // check that could not measure must not claim a shortage either.
            if (status.FreeBytes is null)
            {
                Assert.Equal(TftpRootVerdict.Writable, status.Verdict);
                return;
            }

            Assert.Equal(TftpRootVerdict.LowSpace, status.Verdict);
            Assert.False(status.IsClear);
            Assert.Contains("free", status.Summary, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void ReportsAFileWhereAFolderWasExpected()
    {
        string file = Path.Combine(Path.GetTempPath(), "netcontrol-tests-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(file, "not a folder");

        try
        {
            TftpRootStatus status = TftpRootCheck.Inspect(file);

            Assert.Equal(TftpRootVerdict.Missing, status.Verdict);
            Assert.True(status.Exists);
            Assert.Contains("a file, not a folder", status.Summary, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
