using System.Globalization;
using NetControl.Core.Oui;

namespace NetControl.OuiPacker;

/// <summary>
/// Where the registry comes from and how to read it.
///
/// IEEE publishes four files. MA-L is the classic 24-bit OUI everyone means when they say "OUI".
/// MA-M (28-bit) and MA-S (36-bit) are the smaller blocks sold to companies that do not need
/// sixteen million addresses, and they are carved out of MA-L ranges that the MA-L file lists as
/// belonging to the "IEEE Registration Authority" - which is why all of them have to be packed
/// together for a lookup to give a useful answer. IAB is the retired predecessor of MA-S; it is
/// also 36-bit, no new blocks are issued, and the assignments made before it closed are still in
/// equipment that is still in panels.
/// </summary>
internal static class IeeeRegistry
{
    public static readonly RegistryFile[] Files =
    [
        new("oui.csv", "https://standards-oui.ieee.org/oui/oui.csv", Required: true),
        new("mam.csv", "https://standards-oui.ieee.org/oui28/mam.csv", Required: false),
        new("oui36.csv", "https://standards-oui.ieee.org/oui36/oui36.csv", Required: false),
        new("iab.csv", "https://standards-oui.ieee.org/iab/iab.csv", Required: false),
    ];

    /// <summary>
    /// Reads one registry CSV. Columns are Registry, Assignment, Organization Name,
    /// Organization Address; only the middle two matter here.
    /// </summary>
    /// <param name="path">A file in IEEE CSV format. The curated seed uses the same format.</param>
    /// <param name="skipped">Rows that were not usable, for the caller to report.</param>
    public static List<OuiAssignment> Read(string path, out int skipped)
    {
        var assignments = new List<OuiAssignment>();
        skipped = 0;

        foreach (string line in File.ReadLines(path))
        {
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var fields = CsvFields.Split(line);
            if (fields.Count < 3)
            {
                skipped++;
                continue;
            }

            // The header row, and nothing else, starts with the literal word "Registry".
            if (fields[0].Equals("Registry", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string hex = fields[1].Trim();
            string organization = fields[2].Trim();

            if (organization.Length == 0 || !TryPrefix(hex, out ulong prefix, out int prefixBits))
            {
                skipped++;
                continue;
            }

            assignments.Add(new OuiAssignment(prefix, prefixBits, organization));
        }

        return assignments;
    }

    /// <summary>
    /// Turns an assignment like "001D9C" or "70B3D51F2" into a masked 48-bit prefix.
    ///
    /// The length of the hex string is what states the block size: six characters is MA-L, seven
    /// is MA-M, nine is MA-S or IAB. The Registry column says the same thing, but the length is
    /// the value actually being packed, so deriving the prefix length from it means the two cannot
    /// disagree.
    /// </summary>
    public static bool TryPrefix(string hex, out ulong prefix, out int prefixBits)
    {
        prefix = 0;
        prefixBits = hex.Length * 4;

        if (prefixBits is not (24 or 28 or 36))
        {
            prefixBits = 0;
            return false;
        }

        if (!ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong value))
        {
            prefixBits = 0;
            return false;
        }

        // Left-align the assignment in the 48-bit address space: 00-1D-9C becomes 00:1D:9C:00:00:00.
        prefix = value << (48 - prefixBits);
        return true;
    }

    /// <summary>
    /// Downloads the registry files into <paramref name="directory"/>.
    ///
    /// Plant and corporate networks being what they are, this is the step most likely to fail, so
    /// the failure message names the URL: downloading the four files in a browser and running
    /// `pack --from` against the folder is a perfectly good fallback and should not require
    /// reading the source to discover.
    /// </summary>
    public static async Task<int> DownloadAsync(string directory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

        // IEEE serves a redirect or a block page to clients that send no User-Agent.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("NetControl.OuiPacker/1.0");

        int downloaded = 0;

        foreach (var file in Files)
        {
            string destination = Path.Combine(directory, file.Name);
            Console.WriteLine($"  {file.Url}");

            try
            {
                byte[] body = await http.GetByteArrayAsync(file.Url, cancellationToken).ConfigureAwait(false);
                await File.WriteAllBytesAsync(destination, body, cancellationToken).ConfigureAwait(false);
                Console.WriteLine($"    -> {file.Name}, {body.Length:N0} bytes");
                downloaded++;
            }
            catch (Exception ex) when (ex is HttpRequestException
                || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                if (file.Required)
                {
                    throw new InvalidOperationException(
                        $"Could not download {file.Url} - {ex.Message}. Download it in a browser, "
                        + $"save it as {destination}, and run: pack --from {directory}",
                        ex);
                }

                Console.WriteLine($"    -> skipped, {ex.Message}");
            }
        }

        return downloaded;
    }
}

/// <summary>One of the IEEE registry files.</summary>
/// <param name="Name">Local file name, also what `pack --from` looks for.</param>
/// <param name="Url">Where IEEE publishes it.</param>
/// <param name="Required">
/// True only for the MA-L file. Losing the smaller registries costs accuracy for small vendors;
/// losing MA-L means there is no table worth packing.
/// </param>
internal sealed record RegistryFile(string Name, string Url, bool Required);
