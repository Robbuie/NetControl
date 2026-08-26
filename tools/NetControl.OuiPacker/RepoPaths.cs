namespace NetControl.OuiPacker;

/// <summary>
/// Finds the repository from wherever the tool happens to be running.
///
/// The point of `update` with no arguments is that refreshing the registry is one command typed
/// from anywhere in the tree. That means the tool has to locate src/NetControl.Core/Oui/oui.bin
/// itself, and the solution file is the only landmark guaranteed to sit at the root.
/// </summary>
internal static class RepoPaths
{
    private const string SolutionFileName = "NetControl.sln";

    /// <summary>The directory containing NetControl.sln, searching upwards from the running exe.</summary>
    public static string Root
    {
        get
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
                 directory is not null;
                 directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, SolutionFileName)))
                {
                    return directory.FullName;
                }
            }

            throw new InvalidOperationException(
                $"Could not find {SolutionFileName} above {AppContext.BaseDirectory}. "
                + "Pass --out explicitly to say where the packed table should go.");
        }
    }

    /// <summary>The embedded resource the engine reads. This is the file `update` rewrites.</summary>
    public static string PackedTable =>
        Path.Combine(Root, "src", "NetControl.Core", "Oui", "oui.bin");

    /// <summary>
    /// The curated seed registry. Prefers the copy beside the exe so the tool still works when it
    /// has been published somewhere else, and falls back to the one in the repo.
    /// </summary>
    public static string SeedCsv
    {
        get
        {
            string beside = Path.Combine(AppContext.BaseDirectory, "seed", "seed-oui.csv");
            return File.Exists(beside)
                ? beside
                : Path.Combine(Root, "tools", "NetControl.OuiPacker", "seed", "seed-oui.csv");
        }
    }
}
