using System.Globalization;

namespace NetControl.Core.Tftp;

/// <summary>
/// Inspects the folder a TFTP server writes into, when the tool is running on the server.
///
/// <para><b>The write is the test.</b> Every permission API on Windows answers a question about
/// the ACL, which is not the same question as "will a file land here": share permissions, a
/// read-only attribute, a full volume, a quota, an antivirus filter driver and a network path
/// that has quietly gone away all produce a folder with a perfectly good ACL that refuses the
/// write. So this creates a small, obviously-named probe file and removes it again, which is the
/// same reasoning the commissioner uses when it reads an address back off a device rather than
/// trusting a success code.</para>
///
/// <para>The probe file is written into the folder the caller named and nowhere else, it is
/// zero-length, and it is removed in a <c>finally</c>. A leftover probe file means the tool was
/// killed mid-check; it is named so that is obvious.</para>
/// </summary>
public static class TftpRootCheck
{
    /// <summary>
    /// The prefix on the probe file. Deliberately long and self-explanatory: if one is ever left
    /// behind, whoever finds it in a backup folder should be able to tell what it was without
    /// asking anybody.
    /// </summary>
    public const string ProbeFilePrefix = "netcontrol-write-probe-";

    /// <summary>
    /// Below this, a robot image may not fit. FANUC images run from a few megabytes to a few
    /// hundred, so any single threshold is a judgement rather than a fact - this one is set to
    /// warn rather than block, and the free figure is always reported beside it so the reader can
    /// apply their own.
    /// </summary>
    public const long DefaultMinimumFreeBytes = 512L * 1024 * 1024;

    /// <summary>
    /// Grades a folder. Never throws: this runs beside the other readiness checks, and one that
    /// throws takes the bar down with it.
    /// </summary>
    /// <param name="path">The server's root folder. Null or blank means none is configured.</param>
    /// <param name="minimumFreeBytes">Free space below which to warn.</param>
    public static TftpRootStatus Inspect(string? path, long minimumFreeBytes = DefaultMinimumFreeBytes)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return TftpRootStatus.NotConfigured();
        }

        try
        {
            if (!Directory.Exists(path))
            {
                string what = File.Exists(path) ? "a file, not a folder" : "nothing at all";

                return new TftpRootStatus(
                    path,
                    TftpRootVerdict.Missing,
                    File.Exists(path),
                    null,
                    $"The TFTP root folder is {what}. TFTP cannot create directories, so a server pointed "
                    + "here refuses every write with a file-not-found error.",
                    "Create the folder, or point the server at the one that already holds the backups.");
            }

            long? free = TryGetFreeBytes(path);

            if (!TryProbeWrite(path, out string? refusal))
            {
                return new TftpRootStatus(
                    path,
                    TftpRootVerdict.NotWritable,
                    true,
                    free,
                    $"The TFTP root folder exists but this account cannot write into it: {refusal}",
                    "Check that the account the TFTP server runs as - which is often a service account "
                    + "rather than the one you are signed in with - has write permission here.");
            }

            if (free is { } bytes && bytes < minimumFreeBytes)
            {
                return new TftpRootStatus(
                    path,
                    TftpRootVerdict.LowSpace,
                    true,
                    free,
                    $"The TFTP root folder is writable, but only {Megabytes(bytes)} is free. A robot image "
                    + "can be several hundred megabytes, and a transfer that runs out of room part way "
                    + "leaves a truncated file behind.",
                    "Free space on this volume, or move the backup folder to one with more room.");
            }

            return new TftpRootStatus(
                path,
                TftpRootVerdict.Writable,
                true,
                free,
                free is { } room
                    ? $"The TFTP root folder is writable, with {Megabytes(room)} free."
                    : "The TFTP root folder is writable. Free space could not be established for this path.",
                null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException)
        {
            return TftpRootStatus.Unknown(path, ex.Message);
        }
    }

    /// <summary>
    /// Creates and removes a zero-length file. Returns false with the reason when the folder
    /// refuses it.
    /// </summary>
    private static bool TryProbeWrite(string path, out string? refusal)
    {
        string probe = Path.Combine(
            path,
            ProbeFilePrefix + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tmp");

        try
        {
            // DeleteOnClose as well as the finally: if this process is killed between the two, the
            // file still goes away, and a backup folder never accumulates our litter.
            using (new FileStream(
                probe,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose))
            {
            }

            refusal = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
        {
            refusal = ex.Message;
            return false;
        }
        finally
        {
            TryDelete(probe);
        }
    }

    private static void TryDelete(string probe)
    {
        try
        {
            if (File.Exists(probe))
            {
                File.Delete(probe);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing useful to do. The file is zero-length, named for what it is, and
            // DeleteOnClose has already had its turn.
        }
    }

    /// <summary>
    /// Free space on the volume, or null for a path where the question does not have an answer -
    /// a UNC share is the usual case, and reporting a guess would be worse than reporting nothing.
    /// </summary>
    private static long? TryGetFreeBytes(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));

            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return null;
            }

            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string Megabytes(long bytes) =>
        (bytes / (1024.0 * 1024.0)).ToString("N0", CultureInfo.InvariantCulture) + " MB";
}
