using NetControl.Core.Diagnostics;
using NetControl.Core.Persistence;
using Xunit;

namespace NetControl.Tests;

/// <summary>
/// The diagnostic log - what the tool did, as opposed to what was done to the equipment.
///
/// <para>The rule under test everywhere here is that it cannot make things worse. It runs inside
/// the crash handlers, so a log that throws would replace a reportable fault with an unreportable
/// one, and a log that filled a plant laptop's disk would be a fault of its own.</para>
/// </summary>
public sealed class TraceLogTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "netcontrol-trace-" + Guid.NewGuid().ToString("N"));

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
            // A temp folder that would not delete is not a failing test.
        }
    }

    [Fact]
    public void WritesTimestampedLinesToAFileItCanName()
    {
        var time = new TestTimeProvider(Events.At);

        using (TraceLog log = TraceLog.Open(_directory, timeProvider: time))
        {
            Assert.False(log.IsDisabled);
            Assert.NotNull(log.FilePath);

            log.Write(EventSeverity.Warn, "UDP/67 is held by vmnetdhcp.exe.");
        }

        string text = File.ReadAllText(Path.Combine(_directory, "netcontrol-20260807.log"));

        Assert.Contains("2026-08-07 09:30:00.000Z", text, StringComparison.Ordinal);
        Assert.Contains("WARN", text, StringComparison.Ordinal);
        Assert.Contains("vmnetdhcp.exe", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The whole exception, stack included. This file exists for the times the one-line message
    /// turned out not to be enough, and a log holding only messages is one of those times.
    /// </summary>
    [Fact]
    public void WritesTheWholeExceptionUnderTheLine()
    {
        using (TraceLog log = TraceLog.Open(_directory, timeProvider: new TestTimeProvider(Events.At)))
        {
            log.Write(EventSeverity.Error, "Could not open the project.", new InvalidOperationException("boom"));
        }

        string text = File.ReadAllText(Path.Combine(_directory, "netcontrol-20260807.log"));

        Assert.Contains("Could not open the project.", text, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", text, StringComparison.Ordinal);
        Assert.Contains("boom", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// One event is one line, so the file can be searched with a plain text tool. A message that
    /// arrives with newlines in it - and Core's messages carry remediation text - would otherwise
    /// make a grep for a timestamp miss half of what it said.
    /// </summary>
    [Fact]
    public void KeepsOneEventOnOneLine()
    {
        using (TraceLog log = TraceLog.Open(_directory, timeProvider: new TestTimeProvider(Events.At)))
        {
            log.Write(EventSeverity.Info, "First line.\r\nSecond line.\nThird line.");
        }

        string[] lines = File.ReadAllLines(Path.Combine(_directory, "netcontrol-20260807.log"));

        Assert.Contains(lines, line => line.Contains("First line. | Second line. | Third line.", StringComparison.Ordinal));
    }

    /// <summary>A new day is a new file, so a week of history is a week of files.</summary>
    [Fact]
    public void RollsToANewFileTheNextDay()
    {
        var time = new TestTimeProvider(Events.At);

        using (TraceLog log = TraceLog.Open(_directory, timeProvider: time))
        {
            log.Write(EventSeverity.Info, "Today.");
            time.Advance(TimeSpan.FromDays(1));
            log.Write(EventSeverity.Info, "Tomorrow.");
        }

        Assert.Contains("Today.", File.ReadAllText(Path.Combine(_directory, "netcontrol-20260807.log")), StringComparison.Ordinal);
        Assert.Contains("Tomorrow.", File.ReadAllText(Path.Combine(_directory, "netcontrol-20260808.log")), StringComparison.Ordinal);
    }

    /// <summary>
    /// A tool left running on a bench for a week must not fill the disk, so a full file rolls to
    /// the next one and old files are deleted.
    /// </summary>
    [Fact]
    public void RollsOnSizeAndKeepsOnlyTheRetentionCount()
    {
        var time = new TestTimeProvider(Events.At);
        string filler = new('x', 2000);

        using (TraceLog log = TraceLog.Open(_directory, timeProvider: time, keepFiles: 2, maxBytes: 4096))
        {
            for (int i = 0; i < 40; i++)
            {
                log.Write(EventSeverity.Info, filler);
            }
        }

        string[] files = Directory.GetFiles(_directory, "netcontrol-*.log");

        // More than one file, because it rolled - and not all of them, because it pruned.
        Assert.InRange(files.Length, 2, 3);
    }

    /// <summary>
    /// <b>The one that matters.</b> A folder that cannot be created disables the log and says why,
    /// and every later write is a no-op rather than an exception - because this runs inside the
    /// crash handlers, where throwing would lose the fault it was called to report.
    /// </summary>
    [Fact]
    public void DisablesItselfRatherThanThrowWhenTheFolderCannotBeUsed()
    {
        // A file where the folder should be: Directory.CreateDirectory refuses, as would a
        // read-only profile or a full disk.
        Directory.CreateDirectory(_directory);
        string blocked = Path.Combine(_directory, "not-a-folder");
        File.WriteAllText(blocked, "in the way");

        using TraceLog log = TraceLog.Open(blocked);

        Assert.True(log.IsDisabled);
        Assert.NotNull(log.LastFailure);
        Assert.Null(log.FilePath);

        // And it stays quiet rather than throwing on every subsequent call.
        log.Write(EventSeverity.Error, "Something went wrong.", new InvalidOperationException("boom"));
        log.Dispose();
    }

    /// <summary>
    /// Started twice in a morning, the tool leaves one readable account of the morning rather than
    /// only the second half of it.
    /// </summary>
    [Fact]
    public void AppendsToTheDaysFileRatherThanTruncatingIt()
    {
        var time = new TestTimeProvider(Events.At);

        using (TraceLog first = TraceLog.Open(_directory, timeProvider: time))
        {
            first.Write(EventSeverity.Info, "First run.");
        }

        using (TraceLog second = TraceLog.Open(_directory, timeProvider: time))
        {
            second.Write(EventSeverity.Info, "Second run.");
        }

        string text = File.ReadAllText(Path.Combine(_directory, "netcontrol-20260807.log"));

        Assert.Contains("First run.", text, StringComparison.Ordinal);
        Assert.Contains("Second run.", text, StringComparison.Ordinal);
    }
}
