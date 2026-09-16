using System.Text;
using NetControl.App.Diagnostics;
using Xunit;

namespace NetControl.Tests;

/// <summary>
/// Replacing a portable copy of NetControl with a downloaded one.
///
/// <para>The swap is tested and the launch is not, deliberately - <c>SwapPortable</c> takes the
/// launcher as a parameter for exactly that reason. Failing to open a window is recoverable by
/// double-clicking something; a rename and a copy that go wrong halfway leave a laptop with no
/// NetControl on it, on a bench, with a panel waiting. So the cases below are the failure ones.
/// </para>
///
/// <para>The installer path has no test here at all, and nothing pretends otherwise: it consists of
/// starting an Inno Setup executable and letting the Restart Manager close this process. There is
/// nothing in it a test could assert that would not be asserting about the stub. It is in the
/// "seen working once" column until a bench session puts it there properly.</para>
/// </summary>
public class UpdateApplierTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "netcontrol-tests", Path.GetRandomFileName());

    public UpdateApplierTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A test that cannot tidy up is not a test that failed.
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The ordinary swap: the new build takes the running one's name, and the running one is still
    /// there under another. Both halves matter - the second is what a rollback needs, and it is
    /// what somebody needs when the new build turns out to be worse.
    /// </summary>
    [Fact]
    public void PutsTheNewBuildInPlaceAndKeepsTheOldOneBeside()
    {
        string running = Write("NetControl.exe", "the build that is running");
        string fresh = Write("downloaded.exe", "the build that was published");

        UpdateApplyResult result = UpdateApplier.SwapPortable(
            UpdateDownload.Verified(fresh, "irrelevant"), running, launch: _ => true);

        Assert.Equal(UpdateApplyOutcome.Restarting, result.Outcome);
        Assert.Equal("the build that was published", File.ReadAllText(running));
        Assert.Equal(
            "the build that is running",
            File.ReadAllText(running + UpdateApplier.SupersededSuffix));
    }

    /// <summary>
    /// A second update in one session finds the previous build still sitting there - it is only
    /// swept at startup - and must not be stopped by it.
    /// </summary>
    [Fact]
    public void ReplacesASupersededBuildLeftByAnEarlierUpdate()
    {
        string running = Write("NetControl.exe", "second");
        Write("NetControl.exe" + UpdateApplier.SupersededSuffix, "first");
        string fresh = Write("downloaded.exe", "third");

        UpdateApplyResult result = UpdateApplier.SwapPortable(
            UpdateDownload.Verified(fresh, "irrelevant"), running, launch: _ => true);

        Assert.Equal(UpdateApplyOutcome.Restarting, result.Outcome);
        Assert.Equal("third", File.ReadAllText(running));
        Assert.Equal("second", File.ReadAllText(running + UpdateApplier.SupersededSuffix));
    }

    /// <summary>
    /// <b>The one that matters.</b> If the copy fails after the rename, the executable must go back
    /// under its own name - otherwise an update that did not happen becomes a tool that is no
    /// longer on the laptop.
    ///
    /// <para>Arranged by pointing the applier at a downloaded file that is not there, which is the
    /// cheapest way to make the copy fail after the move has succeeded.</para>
    /// </summary>
    [Fact]
    public void PutsTheRunningBuildBackWhenTheCopyFails()
    {
        string running = Write("NetControl.exe", "the build that is running");
        string missing = Path.Combine(_folder, "never-downloaded.exe");

        UpdateApplyResult result = UpdateApplier.SwapPortable(
            UpdateDownload.Verified(missing, "irrelevant"), running, launch: _ => true);

        Assert.Equal(UpdateApplyOutcome.Failed, result.Outcome);
        Assert.True(File.Exists(running), "NetControl.exe must be back under its own name.");
        Assert.Equal("the build that is running", File.ReadAllText(running));
        Assert.False(File.Exists(running + UpdateApplier.SupersededSuffix));

        // And says where the download is, so a failure here is a manual install rather than a dead end.
        Assert.Contains("Nothing has changed", result.Problem!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A refused download never reaches the file work. The type it arrives in cannot hold both a
    /// path and a problem, so this is belt and braces - but this is the call that runs an
    /// executable, and belt and braces is the right amount of care for it.
    /// </summary>
    [Fact]
    public void RefusesToApplyADownloadThatWasNotVerified()
    {
        string running = Write("NetControl.exe", "the build that is running");

        UpdateApplyResult result = UpdateApplier.SwapPortable(
            UpdateDownload.Refused("the checksum did not match"), running, launch: _ => true);

        Assert.Equal(UpdateApplyOutcome.Failed, result.Outcome);
        Assert.Equal("the build that is running", File.ReadAllText(running));
        Assert.Contains("checksum", result.Problem!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A process that cannot say where it is running from replaces nothing. Guessing would mean
    /// writing an executable to a path arrived at by assumption.
    /// </summary>
    [Fact]
    public void ReplacesNothingWhenItCannotSayWhereItIsRunningFrom()
    {
        string fresh = Write("downloaded.exe", "the build that was published");

        UpdateApplyResult result = UpdateApplier.SwapPortable(
            UpdateDownload.Verified(fresh, "irrelevant"), runningExe: null, launch: _ => true);

        Assert.Equal(UpdateApplyOutcome.Failed, result.Outcome);
        Assert.Contains(fresh, result.Problem!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A swap that worked but would not start is <b>not</b> rolled back. The new build is in place
    /// and is correct; putting the old one back over it would undo a successful update because a
    /// window failed to open.
    /// </summary>
    [Fact]
    public void KeepsTheNewBuildWhenItWasSwappedInButWouldNotStart()
    {
        string running = Write("NetControl.exe", "the build that is running");
        string fresh = Write("downloaded.exe", "the build that was published");

        UpdateApplyResult result = UpdateApplier.SwapPortable(
            UpdateDownload.Verified(fresh, "irrelevant"), running, launch: _ => false);

        Assert.Equal(UpdateApplyOutcome.Failed, result.Outcome);
        Assert.Equal("the build that was published", File.ReadAllText(running));
        Assert.Contains("Run it by hand", result.Problem!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The sweep clears the previous executable and the updates folder, and - the part actually
    /// worth asserting - it is silent about a folder that is not there. It runs during startup,
    /// where anything that throws is the tool not opening.
    /// </summary>
    [Fact]
    public void SweepsThePreviousBuildAndSaysNothingAboutAFolderThatIsNotThere()
    {
        string running = Write("NetControl.exe", "current");
        Write("NetControl.exe" + UpdateApplier.SupersededSuffix, "previous");

        string updates = Path.Combine(_folder, "updates");
        Directory.CreateDirectory(updates);
        File.WriteAllText(Path.Combine(updates, "NetControl-Setup-0.6.0.exe"), "downloaded");

        UpdateApplier.SweepLeftovers(running, updates);

        Assert.False(File.Exists(running + UpdateApplier.SupersededSuffix));
        Assert.Empty(Directory.GetFiles(updates));
        Assert.True(File.Exists(running), "the running build is not a leftover.");

        // Twice over, and against a folder that was never created. Neither throws.
        UpdateApplier.SweepLeftovers(running, updates);
        UpdateApplier.SweepLeftovers(running, Path.Combine(_folder, "never-existed"));
        UpdateApplier.SweepLeftovers(null, updates);
    }

    private string Write(string name, string content)
    {
        string path = Path.Combine(_folder, name);
        File.WriteAllText(path, content, Encoding.UTF8);
        return path;
    }
}
