using NetControl.Core.Diagnostics;
using NetControl.Core.Persistence;
using Xunit;

namespace NetControl.Tests;

/// <summary>The detailed trace, the in-app viewer's buffer, and the cap that keeps verbose cheap.</summary>
public sealed class TraceLogVerboseTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "netcontrol-trace-v-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Not a failing test.
        }
    }

    [Fact]
    public void DetailIsWrittenOnlyWhileVerboseIsOn()
    {
        var time = new TestTimeProvider(Events.At);
        string path;

        using (TraceLog log = TraceLog.Open(_directory, timeProvider: time))
        {
            int built = 0;
            log.Debug(() => { built++; return "hidden"; });
            Assert.Equal(0, built);

            log.IsVerbose = true;
            log.Debug(() => "SEND Ping -> 10.0.0.5");
            path = log.FilePath!;
        }

        string text = File.ReadAllText(path);
        Assert.DoesNotContain("hidden", text, StringComparison.Ordinal);
        Assert.Contains("DEBUG  SEND Ping -> 10.0.0.5", text, StringComparison.Ordinal);
    }

    [Fact]
    public void PastThePerSecondAllowanceDetailIsSummarisedNotWritten()
    {
        var time = new TestTimeProvider(Events.At);
        string path;

        using (TraceLog log = TraceLog.Open(_directory, timeProvider: time))
        {
            log.IsVerbose = true;
            for (int i = 0; i < TraceLog.VerboseLinesPerSecond + 50; i++)
            {
                log.WriteVerbose($"line {i}");
            }

            time.Advance(TimeSpan.FromSeconds(1));
            log.WriteVerbose("next second");
            path = log.FilePath!;
        }

        string[] lines = File.ReadAllLines(path);
        Assert.Equal(TraceLog.VerboseLinesPerSecond, lines.Count(l => l.Contains("DEBUG  line ", StringComparison.Ordinal)));
        Assert.Contains(lines, l => l.Contains("50 verbose line(s) were not written", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("next second", StringComparison.Ordinal));
    }

    [Fact]
    public void RecentKeepsTheNewestLinesForTheViewerAndRaisesEachOne()
    {
        using TraceLog log = TraceLog.Open(_directory, timeProvider: new TestTimeProvider(Events.At));
        var seen = new List<TraceLine>();
        log.LineWritten += (_, line) => seen.Add(line);

        log.Write(EventSeverity.Warn, "first");
        log.Write(EventSeverity.Error, "second", new InvalidOperationException("boom"));

        IReadOnlyList<TraceLine> recent = log.Recent();
        Assert.Equal("second", recent[^1].Message);
        Assert.Equal(TraceLine.Error, recent[^1].Level);
        Assert.Contains("boom", recent[^1].Detail, StringComparison.Ordinal);
        Assert.Equal(["first", "second"], seen.Select(l => l.Message));
    }

    [Fact]
    public void ADisabledLogStillFeedsTheViewer()
    {
        // A file that cannot be written must not take the in-app view down with it.
        string blocked = Path.Combine(_directory, "file-not-folder");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(blocked, "x");

        using TraceLog log = TraceLog.Open(blocked, timeProvider: new TestTimeProvider(Events.At));
        log.Write(EventSeverity.Info, "still here");

        Assert.True(log.IsDisabled);
        Assert.Contains(log.Recent(), l => l.Message == "still here");
    }
}
