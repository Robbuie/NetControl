using System.Diagnostics.CodeAnalysis;

namespace NetControl.Core.Tftp;

/// <summary>
/// Turns the filename a controller asked for into a path inside the folder Accept mode was given -
/// or into the reason it cannot be one.
///
/// <para><b>Never outside the folder.</b> TFTP has no authentication at all, so the only thing
/// standing between a request and the rest of the disk is this check. A drive letter, a <c>..</c>,
/// or anything that resolves outside the folder after normalisation is refused.</para>
///
/// <para>The refusals are worded as diagnoses, because each one is a real server's refusal too:
/// a controller asking for <c>ROBOT1/FROM00.IMG</c> when the server has no <c>ROBOT1</c> folder is
/// refused by every TFTP server, and saying exactly that is the five-second fix the watch exists for.</para>
/// </summary>
public static class TftpAcceptPath
{
    public static bool TryResolve(
        string folder,
        string requested,
        [NotNullWhen(true)] out string? path,
        [NotNullWhen(false)] out string? problem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(requested);

        path = null;

        if (requested.Length == 0)
        {
            problem = "The request carries no filename.";
            return false;
        }

        // TFTP clients use either separator. Windows takes both, but normalising first is what lets
        // the checks below see every segment.
        string relative = requested.Replace('/', '\\').TrimStart('\\');

        if (relative.Contains(':', StringComparison.Ordinal))
        {
            problem = $"'{requested}' names a drive or a stream. A TFTP server only accepts names relative "
                + "to its root folder.";
            return false;
        }

        string[] segments = relative.Split('\\');
        foreach (string segment in segments)
        {
            if (segment.Length == 0)
            {
                problem = $"'{requested}' has an empty folder name in it.";
                return false;
            }

            if (segment is "." or "..")
            {
                problem = $"'{requested}' climbs out of the folder with '{segment}'. Refused, as any server "
                    + "should.";
                return false;
            }

            if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                problem = $"'{requested}' contains characters Windows does not allow in a file name.";
                return false;
            }

            // Windows drops a trailing space or dot without a word, so the file would land under a
            // different name from the one asked for - and the next restore would ask for a file
            // that is not there.
            if (segment.EndsWith(' ') || segment.EndsWith('.'))
            {
                problem = $"'{requested}' has a name ending in a space or a dot, which Windows silently "
                    + "drops. The file would be saved under a different name from the one the controller "
                    + "will ask for when it restores.";
                return false;
            }
        }

        string root = Path.GetFullPath(folder);
        if (!root.EndsWith(Path.DirectorySeparatorChar))
        {
            root += Path.DirectorySeparatorChar;
        }

        string full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            problem = $"'{requested}' resolves outside the folder. Refused.";
            return false;
        }

        string? directory = Path.GetDirectoryName(full);
        if (directory is null || !Directory.Exists(directory))
        {
            string missing = directory is null ? relative : Path.GetRelativePath(root, directory);
            problem = $"'{requested}' asks for the folder '{missing}', which does not exist in {root.TrimEnd('\\')}. "
                + "TFTP cannot create folders, so a real server refuses this the same way - create the "
                + "folder on the server, or change the path set on the controller.";
            return false;
        }

        path = full;
        problem = null;
        return true;
    }
}
