// UseWPF drops System.IO from the implicit usings; this file writes a zip. See CLAUDE.md.
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using NetControl.Core.Analysis;
using NetControl.Core.Diagnostics;
using NetControl.Core.Interfaces;
using NetControl.Core.Persistence;

namespace NetControl.App.Diagnostics;

/// <summary>
/// Everything somebody else needs to work out what happened, in one zip: the diagnostic logs, this
/// PC's adapters, the sent-packet ledger, the last troubleshoot, the project's event record as CSV,
/// and the tool's own settings files.
///
/// <para><b>What is not in it:</b> the project file itself (the plan, which may be a customer's
/// network) and anything outside NetControl's own folder. The event record is included because it
/// is usually the evidence; the bundle's README says so, so whoever sends it knows what they are
/// sending.</para>
/// </summary>
public static class SupportBundle
{
    /// <summary>The settings files worth including, by name under the data folder.</summary>
    public static readonly string[] SettingsFiles =
        ["settings.json", "appearance.json", "tftp.json", "safety.json", "adapter-snapshots.json"];

    /// <summary>Writes the bundle. Returns null when written, otherwise why not.</summary>
    public static string? Write(string zipPath, SupportBundleContents contents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);
        ArgumentNullException.ThrowIfNull(contents);

        try
        {
            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }

            using ZipArchive zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);

            Text(zip, "README.txt", Readme(contents));
            Text(zip, "environment.txt", Environment(contents));
            Text(zip, "troubleshoot.txt", Findings(contents.Findings));
            Text(zip, "sent-packets.csv", contents.LedgerCsv);
            Text(zip, "recent-log.txt", Lines(contents.TraceLines));

            if (contents.ProjectEvents is { } events)
            {
                Text(zip, "project-events.csv", EventsCsv(events));
            }

            if (contents.LogsFolder is { } logs && Directory.Exists(logs))
            {
                foreach (string file in Directory.EnumerateFiles(logs, "*.log").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    Copy(zip, file, "logs/" + Path.GetFileName(file));
                }
            }

            if (contents.DataFolder is { } data)
            {
                foreach (string name in SettingsFiles)
                {
                    string path = Path.Combine(data, name);
                    if (File.Exists(path))
                    {
                        Copy(zip, path, "settings/" + name);
                    }
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
            or ArgumentException or InvalidDataException)
        {
            return $"The support bundle could not be written to {zipPath}: {ex.Message}";
        }
    }

    private static void Text(ZipArchive zip, string name, string text)
    {
        ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(text);
    }

    /// <summary>Copies a file the tool may still have open - today's log is being written to.</summary>
    private static void Copy(ZipArchive zip, string path, string name)
    {
        try
        {
            using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using Stream target = entry.Open();
            source.CopyTo(target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Text(zip, name + ".unreadable.txt", $"{path} could not be read: {ex.Message}");
        }
    }

    private static string Readme(SupportBundleContents c) =>
        $"""
        NetControl support bundle
        Written {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} by {c.BuildText}.

        environment.txt     this PC's adapters, which one was selected, UDP/67 and the firewall, network safety settings
        troubleshoot.txt    the last Troubleshoot run on the Network health tab, if one was run
        sent-packets.csv    everything NetControl transmitted (or was stopped from transmitting) this session
        recent-log.txt      the last lines of the diagnostic log, as the in-app viewer showed them
        project-events.csv  the open project's append-only event record{(c.ProjectEvents is null ? " (not included - no project events)" : string.Empty)}
        logs/               the diagnostic log files - what the tool did, with any errors and stack traces
        settings/           NetControl's own settings files

        Not included: the project file itself (the plan) and anything outside NetControl's folder.
        The event record names devices, addresses and MACs from the open project ({c.ProjectName}).
        """;

    private static string Environment(SupportBundleContents c)
    {
        var text = new StringBuilder();
        text.AppendLine(c.BuildText);
        text.AppendLine(CultureInfo.InvariantCulture, $"OS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        text.AppendLine(CultureInfo.InvariantCulture, $".NET: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Listen only: {(c.ListenOnly ? "on" : "off")}, send cap: {(c.MaxPacketsPerSecond == 0 ? "none" : c.MaxPacketsPerSecond.ToString(CultureInfo.InvariantCulture) + "/s")}, verbose log: {(c.VerboseLog ? "on" : "off")}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Listener: {c.ListenerState}");
        text.AppendLine();

        text.AppendLine(CultureInfo.InvariantCulture, $"Selected adapter: {(c.SelectedAdapter is { } s ? Describe(s) : "none")}");
        text.AppendLine();
        text.AppendLine("All adapters:");
        foreach (NicInfo nic in c.Adapters)
        {
            text.AppendLine("  " + Describe(nic));
        }

        text.AppendLine();
        foreach (string line in c.EnvironmentChecks)
        {
            text.AppendLine(line);
        }

        return text.ToString();
    }

    private static string Describe(NicInfo nic)
    {
        string extra = nic.ExtraAddresses.Count == 0 ? string.Empty : $" + {nic.ExtraAddresses.Count} more address(es)";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"[{nic.Index}] {nic.Name} ({nic.Description}) {nic.Status}, {nic.IPv4?.ToString() ?? "no IPv4"}/{nic.PrefixLength?.ToString(CultureInfo.InvariantCulture) ?? "?"}{extra}, "
            + $"gw {nic.Gateway?.ToString() ?? "none"}, {nic.SpeedBps / 1_000_000} Mb/s, MAC {nic.Mac}{(nic.IsLikelyVirtual ? ", virtual" : string.Empty)}");
    }

    private static string Findings(IReadOnlyList<NetworkFinding>? findings)
    {
        if (findings is null || findings.Count == 0)
        {
            return "Troubleshoot was not run this session.";
        }

        var text = new StringBuilder();
        foreach (NetworkFinding f in findings)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"[{f.SeverityText}] {f.Area}: {f.Title}");
            if (!string.IsNullOrWhiteSpace(f.Detail))
            {
                text.AppendLine("    " + f.Detail);
            }

            if (f.NextStep is { } next)
            {
                text.AppendLine("    Next: " + next);
            }
        }

        return text.ToString();
    }

    private static string Lines(IReadOnlyList<TraceLine> lines)
    {
        var text = new StringBuilder();
        foreach (TraceLine line in lines)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"{line.Utc:yyyy-MM-dd HH:mm:ss.fff}Z  {line.Level,-5}  {line.Message}");
            if (line.Detail is { } detail)
            {
                text.AppendLine(detail);
            }
        }

        return text.ToString();
    }

    private static string EventsCsv(IReadOnlyList<EventRecord> events)
    {
        var text = new StringBuilder("Utc,Severity,Category,DeviceId,Target,Message,Detail\r\n");
        foreach (EventRecord e in events)
        {
            text.Append(e.Utc.ToString("o", CultureInfo.InvariantCulture)).Append(',')
                .Append(e.Severity).Append(',')
                .Append(e.Category).Append(',')
                .Append(e.DeviceId?.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(Quote(e.Target)).Append(',')
                .Append(Quote(e.Message)).Append(',')
                .Append(Quote(e.Detail)).Append("\r\n");
        }

        return text.ToString();
    }

    private static string Quote(string? value) =>
        value is null ? string.Empty
        : value.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
        : value;
}
