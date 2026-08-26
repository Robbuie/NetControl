using NetControl.Core;
using NetControl.Core.Oui;

namespace NetControl.OuiPacker;

/// <summary>
/// What the tool actually does. Four commands, of which `update` is the one anybody runs.
/// </summary>
internal static class Commands
{
    /// <summary>Download the registries and rewrite the embedded table. The whole chore.</summary>
    public static async Task<int> UpdateAsync(CommandLine options, CancellationToken cancellationToken)
    {
        string workingDirectory = options.Value("keep")
            ?? Path.Combine(Path.GetTempPath(), "netcontrol-oui-" + Guid.NewGuid().ToString("N")[..8]);

        Console.WriteLine("Downloading the IEEE registry:");
        await IeeeRegistry.DownloadAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
        Console.WriteLine();

        string output = options.Value("out") ?? RepoPaths.PackedTable;
        int result = PackFrom(workingDirectory, output, options.Date("date", DateTime.UtcNow));

        if (options.Has("keep"))
        {
            Console.WriteLine($"CSV files left in {workingDirectory}");
        }
        else
        {
            TryDeleteDirectory(workingDirectory);
        }

        return result;
    }

    /// <summary>Download the registries and stop, for when packing and fetching want to be separate.</summary>
    public static async Task<int> FetchAsync(CommandLine options, CancellationToken cancellationToken)
    {
        string directory = options.Required("to");
        int count = await IeeeRegistry.DownloadAsync(directory, cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"{count} of {IeeeRegistry.Files.Length} files downloaded to {directory}");
        return 0;
    }

    /// <summary>Pack CSVs that are already on disk, or the curated seed.</summary>
    public static int Pack(CommandLine options)
    {
        string output = options.Value("out") ?? RepoPaths.PackedTable;

        if (options.Has("seed"))
        {
            string seed = RepoPaths.SeedCsv;
            Console.WriteLine($"Packing the curated seed registry from {seed}");

            // The seed's own timestamp would be meaningless - it is a hand-maintained file that
            // gets touched by every checkout. Unix epoch is the honest answer: this data has no
            // registry date, and the UI showing 1970 is a clear signal that nobody has run a
            // real refresh yet.
            return Pack([seed], output, options.Date("date", DateTime.UnixEpoch));
        }

        string from = options.Required("from");
        return PackFrom(from, output, options.Date("date", NewestWriteTime(from)));
    }

    /// <summary>Read a packed table back out as text, for looking at what actually got packed.</summary>
    public static int Dump(CommandLine options)
    {
        string input = options.Value("in") ?? RepoPaths.PackedTable;
        var database = OuiDatabase.LoadFile(input);

        Console.WriteLine($"{input}");
        Console.WriteLine($"  source date  {database.SourceDateUtc:yyyy-MM-dd} UTC");
        Console.WriteLine($"  assignments  {database.Count:N0}"
            + $"  (24-bit {database.CountOf(24):N0}, 28-bit {database.CountOf(28):N0},"
            + $" 36-bit {database.CountOf(36):N0})");
        Console.WriteLine($"  packed size  {new FileInfo(input).Length:N0} bytes");
        Console.WriteLine();

        if (options.Value("mac") is { } macText)
        {
            var mac = MacAddress.Parse(macText);
            string? vendor = database.Lookup(mac, out int prefixBits);

            Console.WriteLine(vendor is null
                ? $"  {mac}  no match"
                : $"  {mac}  {vendor}  (matched {prefixBits} bits)");

            return 0;
        }

        int take = options.Int("take", 40);
        foreach (var assignment in database.Enumerate().Take(take))
        {
            Console.WriteLine($"  {assignment.PrefixText,-14} /{assignment.PrefixBits,-3} {assignment.Organization}");
        }

        if (database.Count > take)
        {
            Console.WriteLine($"  ... and {database.Count - take:N0} more (--take N, or --mac to look one up)");
        }

        return 0;
    }

    public static void PrintUsage()
    {
        Console.WriteLine("""
            NetControl.OuiPacker - refreshes the IEEE OUI table embedded in NetControl.Core.

              update [--out FILE] [--date yyyy-MM-dd] [--keep DIR]
                  Download the IEEE registry and rewrite src/NetControl.Core/Oui/oui.bin.
                  This is the one to run. Rebuild afterwards; commit the changed oui.bin.

              fetch --to DIR
                  Download the registry CSVs and stop. For a machine that can reach IEEE but
                  is not the one with the repository on it.

              pack --from DIR [--out FILE] [--date yyyy-MM-dd]
              pack --seed      [--out FILE]
                  Pack CSVs already on disk. --from expects the IEEE file names
                  (oui.csv, mam.csv, oui36.csv, iab.csv); missing ones are skipped except oui.csv.
                  --seed packs the small curated industrial-vendor list instead.

              dump [--in FILE] [--take N]
              dump [--in FILE] --mac 00:1D:9C:C7:B0:70
                  Read a packed table back as text, or look up one address in it.
            """);
    }

    private static int PackFrom(string directory, string output, DateTime sourceDateUtc)
    {
        var inputs = new List<string>();

        foreach (var file in IeeeRegistry.Files)
        {
            string path = Path.Combine(directory, file.Name);

            if (File.Exists(path))
            {
                inputs.Add(path);
            }
            else if (file.Required)
            {
                throw new FileNotFoundException(
                    $"{file.Name} is not in {directory}, and it is the one file the table cannot be built without. "
                    + $"Download {file.Url} into that folder and try again.",
                    path);
            }
            else
            {
                Console.WriteLine($"  {file.Name} is missing - packing without it");
            }
        }

        return Pack(inputs, output, sourceDateUtc);
    }

    private static int Pack(IReadOnlyList<string> inputs, string output, DateTime sourceDateUtc)
    {
        var assignments = new List<OuiAssignment>();

        foreach (string input in inputs)
        {
            var read = IeeeRegistry.Read(input, out int skipped);
            assignments.AddRange(read);

            Console.WriteLine($"  {Path.GetFileName(input),-12} {read.Count,7:N0} assignments"
                + (skipped > 0 ? $", {skipped:N0} rows skipped" : string.Empty));
        }

        if (assignments.Count == 0)
        {
            throw new InvalidOperationException(
                "None of the input files yielded a usable assignment. "
                + "Check that they are the IEEE CSV files and not an HTML error page.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);

        // Written to a temp file and moved into place, so a failure half way through leaves the
        // committed table intact rather than truncated - a build with a corrupt embedded resource
        // is a confusing thing to hand someone.
        string temporary = output + ".tmp";
        int written;

        using (var file = File.Create(temporary))
        {
            written = OuiPackWriter.Write(file, sourceDateUtc, assignments);
        }

        File.Move(temporary, output, overwrite: true);

        var packed = OuiDatabase.LoadFile(output);
        Console.WriteLine();
        Console.WriteLine($"  wrote {output}");
        Console.WriteLine($"  {written:N0} assignments"
            + $" (24-bit {packed.CountOf(24):N0}, 28-bit {packed.CountOf(28):N0}, 36-bit {packed.CountOf(36):N0})"
            + $" in {new FileInfo(output).Length:N0} bytes");
        Console.WriteLine($"  source date {packed.SourceDateUtc:yyyy-MM-dd} UTC");

        // Reading it straight back is the only check that matters: the writer and the reader agree.
        // A silent disagreement here would ship as wrong vendor names, which nobody would question.
        int duplicates = assignments.Count - written;
        if (duplicates > 0)
        {
            Console.WriteLine($"  {duplicates:N0} duplicate prefixes collapsed");
        }

        return 0;
    }

    private static DateTime NewestWriteTime(string directory) =>
        Directory.EnumerateFiles(directory, "*.csv")
            .Select(File.GetLastWriteTimeUtc)
            .DefaultIfEmpty(DateTime.UtcNow)
            .Max();

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp folder is not worth failing a successful pack over.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
