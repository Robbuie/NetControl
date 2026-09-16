// UseWPF drops System.IO from the implicit usings; this file moves files about.
using System.IO;
using NetControl.Core.Diagnostics;

namespace NetControl.App.Diagnostics;

/// <summary>
/// Replaces this copy of NetControl with a downloaded one.
///
/// <para>Two ways, because there are two kinds of copy - see <see cref="InstallKind"/>. Neither
/// uses a helper script. A batch file that waits for a process to exit and then moves files around
/// is the traditional way to do this and it is also the part nobody can test: it runs exactly once,
/// after the application it would report to has closed, so when it goes wrong the symptom is a tool
/// that shut down and never came back, on a bench, with a panel waiting.</para>
///
/// <para>What replaces it is two Windows facts. An installer can shut its own target down through
/// the Restart Manager and start it again afterwards, which is what <c>/CLOSEAPPLICATIONS
/// /RESTARTAPPLICATIONS</c> are. And a running executable can be <em>renamed</em> even though it
/// cannot be overwritten - so a portable copy moves itself aside and copies the new one into the
/// name it just vacated, with the old file still on disk to move back if the copy fails.</para>
///
/// <para>Nothing here throws.</para>
/// </summary>
public static class UpdateApplier
{
    /// <summary>
    /// What the running copy is renamed to while the new one takes its place. Fixed rather than
    /// versioned so there can only ever be one of them, and swept on the next start - see
    /// <see cref="SweepLeftovers"/>.
    /// </summary>
    public const string SupersededSuffix = ".superseded";

    /// <summary>
    /// The switches the silent install runs with.
    ///
    /// <para><c>/CLOSEAPPLICATIONS</c> is what closes this process, and it closes it <b>properly</b>:
    /// the Restart Manager asks a GUI application to shut down the ordinary way first and only
    /// terminates one that will not answer. So WPF raises the normal exit, <c>App.OnExit</c> runs,
    /// and the listening socket and the project file's handle are released the same way they are on
    /// any other close. That is why this method does not shut the application down itself, and must
    /// not: exiting first would leave the Restart Manager nothing to restart.</para>
    ///
    /// <para><c>/NORESTART</c> refuses a reboot, which no per-user install of a single file could
    /// ever need and which would be an outrageous thing to do to a commissioning laptop.</para>
    /// </summary>
    public const string SilentSwitches =
        "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS";

    /// <summary>
    /// Starts the downloaded installer and returns. <b>The caller does not exit</b>; setup closes
    /// this process itself and starts the new build afterwards.
    /// </summary>
    public static UpdateApplyResult RunInstaller(UpdateDownload download, ITraceLog? trace = null)
    {
        ArgumentNullException.ThrowIfNull(download);

        ITraceLog log = trace ?? NullTraceLog.Instance;

        if (download.Path is not { } setup)
        {
            return UpdateApplyResult.Failed(download.Problem ?? "There is nothing to install.");
        }

        try
        {
            log.Info($"Starting {setup} {SilentSwitches}.");

            using System.Diagnostics.Process? started = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(setup, SilentSwitches)
                {
                    // No shell, so nothing here can be turned into an elevation prompt by file
                    // association. A per-user install needs no elevation and must not ask for any.
                    UseShellExecute = false,
                });

            return started is null
                ? UpdateApplyResult.Failed($"{setup} would not start.")
                : UpdateApplyResult.InstallerRunning;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
            or InvalidOperationException or ObjectDisposedException or IOException)
        {
            log.Warn($"{setup} would not start.", ex);

            return UpdateApplyResult.Failed(
                $"The installer would not start: {ex.Message}"
                + Environment.NewLine + Environment.NewLine
                + $"It is downloaded and verified at {setup}, so it can be run by hand.");
        }
    }

    /// <summary>
    /// Swaps a downloaded exe into the place of the running one and starts it. The caller shuts
    /// down as soon as this returns <see cref="UpdateApplyResult.Restarting"/>.
    ///
    /// <para>The order is the whole design. The rename happens first because it is the step most
    /// likely to be refused - a read-only folder, a USB stick pulled out, a copy running from a
    /// network share - and because it is reversible: if the copy that follows it fails, the old
    /// executable is still there under another name and is moved straight back. At no point is
    /// there a moment where neither file exists.</para>
    /// </summary>
    /// <param name="runningExe">Where this process is running from, from <c>BuildInfo.ExecutablePath</c>.</param>
    /// <param name="launch">
    /// How the replacement is started; the real one is <c>Process.Start</c>. Injected so a test can
    /// exercise the swap - the rename, the copy, and above all the rollback - without a process
    /// being started, because the swap is the half that can leave a laptop with no NetControl on it
    /// and the launch is the half that cannot.
    /// </param>
    public static UpdateApplyResult SwapPortable(
        UpdateDownload download,
        string? runningExe,
        ITraceLog? trace = null,
        Func<string, bool>? launch = null)
    {
        ArgumentNullException.ThrowIfNull(download);

        ITraceLog log = trace ?? NullTraceLog.Instance;

        if (download.Path is not { } fresh)
        {
            return UpdateApplyResult.Failed(download.Problem ?? "There is nothing to install.");
        }

        if (string.IsNullOrWhiteSpace(runningExe) || !File.Exists(runningExe))
        {
            return UpdateApplyResult.Failed(
                "This copy of NetControl cannot say where it is running from, so it will not "
                + $"replace anything. The new build is downloaded and verified at {fresh}.");
        }

        string superseded = runningExe + SupersededSuffix;
        bool moved = false;

        try
        {
            // A leftover from a previous update. It is not running, so it deletes.
            Sweep(superseded, log);

            File.Move(runningExe, superseded);
            moved = true;

            // Windows lets a running image be renamed but not overwritten, which is exactly the gap
            // the move above opened.
            File.Copy(fresh, runningExe, overwrite: false);

            log.Info($"{runningExe} replaced; the previous build is at {superseded}.");

            if (!(launch ?? Launch)(runningExe))
            {
                // The swap itself worked, so this is not rolled back: the new build is in place and
                // correct, and putting the old one back over it would undo a successful update
                // because of a failure to open a window.
                return UpdateApplyResult.Failed(
                    $"{runningExe} was replaced and is the new build, but it would not start. "
                    + "Run it by hand.");
            }

            return UpdateApplyResult.Restarting;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException or System.ComponentModel.Win32Exception
            or InvalidOperationException or ObjectDisposedException)
        {
            log.Warn($"{runningExe} could not be replaced.", ex);

            if (moved)
            {
                // Put it back. Getting this wrong is the difference between an update that did not
                // happen and a tool that is no longer on the laptop.
                try
                {
                    if (!File.Exists(runningExe))
                    {
                        File.Move(superseded, runningExe);
                    }
                }
                catch (Exception rollback) when (rollback is IOException
                    or UnauthorizedAccessException or NotSupportedException or ArgumentException)
                {
                    log.Error($"{runningExe} could not be put back from {superseded}.", rollback);

                    return UpdateApplyResult.Failed(
                        $"The update failed and NetControl could not be put back: {ex.Message}"
                        + Environment.NewLine + Environment.NewLine
                        + $"The build you were running is at {superseded}. Rename it back to "
                        + $"{Path.GetFileName(runningExe)} before closing this window.");
                }
            }

            return UpdateApplyResult.Failed(
                $"NetControl could not replace itself: {ex.Message}"
                + Environment.NewLine + Environment.NewLine
                + "Nothing has changed. This usually means the folder it is running from cannot be "
                + $"written to. The new build is downloaded and verified at {fresh}.");
        }
    }

    /// <summary>
    /// Removes the previous executable left beside a portable copy by the last update, and any
    /// finished downloads. Called once at startup.
    ///
    /// <para>It runs at startup rather than at the end of an update because the file it is deleting
    /// is the one the process doing the update was running from - which cannot delete itself, and
    /// can be deleted by anything at all a moment later.</para>
    ///
    /// <para>Best effort and completely silent. A leftover file wastes some disk; a startup that
    /// fails because of one is the tool not opening.</para>
    /// </summary>
    public static void SweepLeftovers(string? runningExe, string updatesFolder, ITraceLog? trace = null)
    {
        ITraceLog log = trace ?? NullTraceLog.Instance;

        if (!string.IsNullOrWhiteSpace(runningExe))
        {
            Sweep(runningExe + SupersededSuffix, log);
        }

        try
        {
            if (!Directory.Exists(updatesFolder))
            {
                return;
            }

            foreach (string file in Directory.EnumerateFiles(updatesFolder))
            {
                Sweep(file, log);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
        {
            log.Info($"{updatesFolder} could not be swept: {ex.Message}");
        }
    }

    private static bool Launch(string path)
    {
        using System.Diagnostics.Process? started = System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = false });

        return started is not null;
    }

    private static void Sweep(string path, ITraceLog log)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                log.Info($"Removed {path}, left over from an update.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
        {
            log.Info($"{path} is still there: {ex.Message}");
        }
    }
}
