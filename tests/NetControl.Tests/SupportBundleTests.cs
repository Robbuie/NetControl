using System.IO.Compression;
using NetControl.App.Diagnostics;
using NetControl.Core.Analysis;
using NetControl.Core.Diagnostics;
using NetControl.Core.Persistence;
using Xunit;

namespace NetControl.Tests;

public sealed class SupportBundleTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "netcontrol-bundle-" + Guid.NewGuid().ToString("N"));

    public SupportBundleTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // Not a failing test.
        }
    }

    [Fact]
    public void HoldsTheLogsTheSettingsAndTheGeneratedFilesButNotTheProject()
    {
        string logs = Directory.CreateDirectory(Path.Combine(_folder, "logs")).FullName;
        File.WriteAllText(Path.Combine(logs, "netcontrol-20261112.log"), "a line");
        File.WriteAllText(Path.Combine(_folder, "safety.json"), "{}");
        File.WriteAllText(Path.Combine(_folder, "plan.netcproj"), "not for the bundle");
        string zip = Path.Combine(_folder, "bundle.zip");

        // Today's log is open for writing while the bundle is made - it must still be read.
        using var held = new FileStream(Path.Combine(logs, "netcontrol-20261112.log"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite);

        string? problem = SupportBundle.Write(zip, new SupportBundleContents
        {
            BuildText = "NetControl 0.15.0",
            Findings = [new NetworkFinding(FindingSeverity.Warn, "Cable", "Linked at 10 Mb/s", "detail", "swap it")],
            LedgerCsv = "Utc,Kind\r\n",
            TraceLines = [new TraceLine(Events.At, TraceLine.Info, "hello")],
            ProjectEvents = [new EventRecord { Utc = Events.At, Category = EventCategory.App, Message = "opened, with a comma" }],
            LogsFolder = logs,
            DataFolder = _folder,
        });

        Assert.Null(problem);
        using ZipArchive archive = ZipFile.OpenRead(zip);
        string[] names = [.. archive.Entries.Select(e => e.FullName)];

        Assert.Contains("logs/netcontrol-20261112.log", names);
        Assert.Contains("settings/safety.json", names);
        Assert.Contains("troubleshoot.txt", names);
        Assert.Contains("project-events.csv", names);
        Assert.DoesNotContain(names, n => n.EndsWith(".netcproj", StringComparison.Ordinal));

        using var reader = new StreamReader(archive.GetEntry("project-events.csv")!.Open());
        Assert.Contains("\"opened, with a comma\"", reader.ReadToEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public void AFolderThatCannotBeWrittenIsReportedNotThrown()
    {
        string? problem = SupportBundle.Write(Path.Combine(_folder, "missing", "deeper", "bundle.zip"), new SupportBundleContents { BuildText = "x" });

        Assert.NotNull(problem);
    }
}
