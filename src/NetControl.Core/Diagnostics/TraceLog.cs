using System.Globalization;
using System.Text;
using NetControl.Core.Persistence;

namespace NetControl.Core.Diagnostics;

/// <summary>
/// A rolling text file recording what the tool did.
///
/// <para><b>Why this is not a logging package.</b> The product is one self-contained exe copied
/// onto a plant laptop, every dependency is a dependency somebody has to justify to plant IT, and
/// the requirement here is small and completely known: timestamped lines, a file that does not grow
/// without limit, a few days of history, and an absolute guarantee that it cannot throw. That is
/// under two hundred lines, so it is under two hundred lines - the same argument that produced
/// <c>CsvFile</c> and the packed OUI table rather than two more packages.</para>
///
/// <para><b>Every ordinary line is flushed.</b> The single most valuable entry in this file is the
/// last one before a crash, and a buffered writer is precisely the thing that loses it. The verbose
/// detail level (<see cref="IsVerbose"/>) is the exception: it can run to hundreds of lines a second,
/// so its lines ride out with the next ordinary line or a two-second timer, and past
/// <see cref="VerboseLinesPerSecond"/> they are summarised rather than written.</para>
///
/// <para><b>It also feeds the in-app viewer</b>: the last <see cref="RecentCapacity"/> lines are kept
/// in memory and each is raised through <see cref="LineWritten"/>, whether or not the file is writable.</para>
///
/// <para><b>It fails silently, once.</b> If the disk is full or the folder is read-only, the log
/// disables itself and records why in <see cref="LastFailure"/> rather than throwing into whatever
/// was being described - which, since this runs inside the crash handlers, would replace a
/// reportable fault with an unreportable one.</para>
///
/// <para>The file is opened <see cref="FileShare.ReadWrite"/> so it can be read while the tool is
/// still running. Somebody standing at a panel with a problem should not have to close the
/// application to find out what it just said.</para>
/// </summary>
public sealed class TraceLog : ITraceLog, IDisposable
{
    /// <summary>Guards the writer, the roll, and the disabled flag. All writes are serialised.</summary>
    private readonly object _gate = new();

    private readonly string _directory;
    private readonly string _prefix;
    private readonly TimeProvider _time;
    private readonly int _keepFiles;
    private readonly long _maxBytes;

    /// <summary>How many recent lines the in-app viewer can scroll back through.</summary>
    public const int RecentCapacity = 2000;

    /// <summary>
    /// Verbose lines allowed per second. A scan of a /22 or a busy capture can produce thousands of
    /// detail lines a second; past this they are counted and summarised in one line instead, so the
    /// detailed trace can never become the thing that slows the tool down or fills the disk.
    /// </summary>
    public const int VerboseLinesPerSecond = 200;

    private readonly TraceLine?[] _recent = new TraceLine?[RecentCapacity];
    private int _recentNext;
    private int _recentCount;

    private volatile bool _verbose;
    private long _verboseWindowStart;
    private int _verboseInWindow;
    private int _verboseDropped;
    private bool _unflushed;
    private Timer? _flushTimer;

    private StreamWriter? _writer;
    private DateOnly _day;
    private long _written;
    private bool _disabled;
    private bool _disposed;

    private TraceLog(string directory, string prefix, TimeProvider time, int keepFiles, long maxBytes)
    {
        _directory = directory;
        _prefix = prefix;
        _time = time;
        _keepFiles = keepFiles;
        _maxBytes = maxBytes;
    }

    /// <summary>The file currently being written, or null when nothing is.</summary>
    public string? FilePath { get; private set; }

    /// <summary>True once something went wrong and the log gave up. See <see cref="LastFailure"/>.</summary>
    public bool IsDisabled => _disabled;

    /// <summary>Why it gave up, for a status line somewhere. Null while it is working.</summary>
    public string? LastFailure { get; private set; }

    /// <summary>
    /// The detailed trace: what each engine sent and received. Off by default. Detail lines are not
    /// flushed one by one - they ride out with the next ordinary line, or within two seconds - which
    /// is what keeps a chatty session from turning into one disk write per packet.
    /// </summary>
    public bool IsVerbose
    {
        get => _verbose;
        set => _verbose = value;
    }

    /// <summary>
    /// Raised after every line is written, on the writing thread. A handler must be quick and must
    /// marshal to the UI itself; one that throws is ignored.
    /// </summary>
    public event EventHandler<TraceLine>? LineWritten;

    /// <summary>The most recent lines, oldest first - up to <see cref="RecentCapacity"/>.</summary>
    public IReadOnlyList<TraceLine> Recent()
    {
        lock (_gate)
        {
            var lines = new List<TraceLine>(_recentCount);
            int start = (_recentNext - _recentCount + RecentCapacity) % RecentCapacity;

            for (int i = 0; i < _recentCount; i++)
            {
                if (_recent[(start + i) % RecentCapacity] is { } line)
                {
                    lines.Add(line);
                }
            }

            return lines;
        }
    }

    /// <summary>
    /// Opens a log in <paramref name="directory"/>, creating it if needed.
    ///
    /// <para>Returns a working log even when the directory cannot be created: it comes back
    /// disabled, with <see cref="LastFailure"/> set. Startup must not fail because a log file could
    /// not be opened.</para>
    /// </summary>
    /// <param name="directory">Where the files go. Created if it does not exist.</param>
    /// <param name="prefix">Leading part of each file name.</param>
    /// <param name="timeProvider">Injected so a test does not have to wait for midnight.</param>
    /// <param name="keepFiles">
    /// How many files to leave behind. Older ones are deleted as new ones are opened - a diagnostic
    /// log that fills a plant laptop's disk is a fault, not a feature.
    /// </param>
    /// <param name="maxBytes">Roll to the next file past this size.</param>
    public static TraceLog Open(
        string directory,
        string prefix = "netcontrol",
        TimeProvider? timeProvider = null,
        int keepFiles = 10,
        long maxBytes = 4L * 1024 * 1024)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentOutOfRangeException.ThrowIfLessThan(keepFiles, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 4096);

        var log = new TraceLog(directory, prefix, timeProvider ?? TimeProvider.System, keepFiles, maxBytes);

        // Opened eagerly so that a folder nobody can write to is discovered at startup - where
        // something can say so - rather than at the moment of the first crash.
        log.Write(EventSeverity.Info, $"Log opened in {directory}.");
        return log;
    }

    public void Write(EventSeverity severity, string message, Exception? error = null)
    {
        if (_disposed)
        {
            return;
        }

        string level = Label(severity).Trim();
        TraceLine? written = null;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            DateTimeOffset at = _time.GetUtcNow();
            written = Remember(new TraceLine(at, level, OneLine(message ?? string.Empty), error?.ToString()));

            if (!_disabled)
            {
                ReportDroppedVerboseLocked(at);
                WriteToFileLocked(severity, message ?? string.Empty, error);
            }
        }

        Raise(written);
    }

    private void WriteToFileLocked(EventSeverity severity, string message, Exception? error)
    {
        if (_disabled)
        {
            return;
        }

        try
        {
            DateTimeOffset now = _time.GetUtcNow();

            if (!EnsureFile(now) || _writer is null)
            {
                return;
            }

            string line = string.Create(
                CultureInfo.InvariantCulture,
                $"{now:yyyy-MM-dd HH:mm:ss.fff}Z  {Label(severity)}  {OneLine(message)}");

            _writer.WriteLine(line);
            _written += line.Length + Environment.NewLine.Length;

            if (error is not null)
            {
                // The whole exception, stack and inner exceptions included. This file exists
                // for the times when the one-line message turned out not to be enough.
                string detail = error.ToString();
                _writer.WriteLine(detail);
                _written += detail.Length + Environment.NewLine.Length;
            }

            // Every ordinary line is flushed - the last one before a crash is the one that matters -
            // and with it any verbose lines still waiting.
            _writer.Flush();
            _unflushed = false;
        }
        catch (Exception ex) when (IsFileTrouble(ex))
        {
            Disable(ex);
        }
    }

    /// <inheritdoc />
    public void WriteVerbose(string message)
    {
        if (!_verbose || _disposed)
        {
            return;
        }

        TraceLine? written = null;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            DateTimeOffset now = _time.GetUtcNow();
            long second = now.ToUnixTimeSeconds();

            if (second != _verboseWindowStart)
            {
                ReportDroppedVerboseLocked(now);
                _verboseWindowStart = second;
                _verboseInWindow = 0;
            }

            if (++_verboseInWindow > VerboseLinesPerSecond)
            {
                _verboseDropped++;
                return;
            }

            written = Remember(new TraceLine(now, TraceLine.Debug, OneLine(message ?? string.Empty)));

            if (!_disabled)
            {
                try
                {
                    if (EnsureFile(now) && _writer is not null)
                    {
                        string line = string.Create(
                            CultureInfo.InvariantCulture,
                            $"{now:yyyy-MM-dd HH:mm:ss.fff}Z  DEBUG  {written.Message}");

                        _writer.WriteLine(line);
                        _written += line.Length + Environment.NewLine.Length;
                        _unflushed = true;
                        _flushTimer ??= new Timer(_ => FlushPending(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
                    }
                }
                catch (Exception ex) when (IsFileTrouble(ex))
                {
                    Disable(ex);
                }
            }
        }

        Raise(written);
    }

    private void FlushPending()
    {
        lock (_gate)
        {
            if (!_unflushed || _writer is null)
            {
                return;
            }

            try
            {
                _writer.Flush();
                _unflushed = false;
            }
            catch (Exception ex) when (IsFileTrouble(ex))
            {
                Disable(ex);
            }
        }
    }

    /// <summary>One summary line for the detail that was over the per-second allowance.</summary>
    private void ReportDroppedVerboseLocked(DateTimeOffset now)
    {
        if (_verboseDropped == 0)
        {
            return;
        }

        int dropped = _verboseDropped;
        _verboseDropped = 0;

        string message = string.Create(
            CultureInfo.InvariantCulture,
            $"{dropped} verbose line(s) were not written - more than {VerboseLinesPerSecond} a second.");

        Remember(new TraceLine(now, TraceLine.Debug, message));

        if (_disabled || _writer is null)
        {
            return;
        }

        try
        {
            string line = string.Create(CultureInfo.InvariantCulture, $"{now:yyyy-MM-dd HH:mm:ss.fff}Z  DEBUG  {message}");
            _writer.WriteLine(line);
            _written += line.Length + Environment.NewLine.Length;
            _unflushed = true;
        }
        catch (Exception ex) when (IsFileTrouble(ex))
        {
            Disable(ex);
        }
    }

    private TraceLine Remember(TraceLine line)
    {
        _recent[_recentNext] = line;
        _recentNext = (_recentNext + 1) % RecentCapacity;
        _recentCount = Math.Min(_recentCount + 1, RecentCapacity);
        return line;
    }

    private void Raise(TraceLine? line)
    {
        if (line is null || LineWritten is not { } handler)
        {
            return;
        }

        try
        {
            handler(this, line);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A viewer that failed is not a reason to fail the thing being logged.
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _flushTimer?.Dispose();
            _flushTimer = null;
            Close();
        }
    }

    /// <summary>
    /// Opens the file for <paramref name="now"/>, rolling to a new one on a new day or a full one.
    /// Returns false when the log has just disabled itself.
    /// </summary>
    private bool EnsureFile(DateTimeOffset now)
    {
        DateOnly today = DateOnly.FromDateTime(now.UtcDateTime);

        if (_writer is not null && today == _day && _written < _maxBytes)
        {
            return true;
        }

        Close();
        _day = today;

        Directory.CreateDirectory(_directory);

        // The day's first file is netcontrol-20260826.log; a second one that day is -2, and so on.
        // Reopening an existing file rather than truncating it is what makes a tool that was
        // started three times this morning leave one readable account rather than the last one.
        for (int index = 1; index <= 999; index++)
        {
            string name = index == 1
                ? string.Create(CultureInfo.InvariantCulture, $"{_prefix}-{today:yyyyMMdd}.log")
                : string.Create(CultureInfo.InvariantCulture, $"{_prefix}-{today:yyyyMMdd}-{index}.log");

            var candidate = new FileInfo(System.IO.Path.Combine(_directory, name));

            if (candidate.Exists && candidate.Length >= _maxBytes)
            {
                continue;
            }

            FilePath = candidate.FullName;
            _written = candidate.Exists ? candidate.Length : 0;

            _writer = new StreamWriter(
                new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            Prune();
            return true;
        }

        Disable(new IOException(
            $"Every log file for {today:yyyy-MM-dd} in {_directory} is already at its size limit."));

        return false;
    }

    /// <summary>
    /// Deletes the oldest files past the retention count. Failure here is ignored on purpose: a log
    /// that stopped recording because it could not tidy up would be exactly backwards.
    /// </summary>
    private void Prune()
    {
        try
        {
            // The file being written is never a candidate. Reopening an older file - which
            // happens whenever the tool is started twice in a day - would otherwise make it the
            // oldest thing in the folder and put it first in line for deletion.
            FileInfo[] files = [.. new DirectoryInfo(_directory)
                .EnumerateFiles($"{_prefix}-*.log")
                .Where(file => !string.Equals(file.FullName, FilePath, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(file => file.LastWriteTimeUtc)];

            // ...so one fewer than the retention count may be deleted from what is left.
            int keep = Math.Max(_keepFiles - 1, 1);

            for (int i = keep; i < files.Length; i++)
            {
                files[i].Delete();
            }
        }
        catch (Exception ex) when (IsFileTrouble(ex))
        {
            // Nothing to do and nowhere to say it - this is the thing that says things.
        }
    }

    private void Disable(Exception ex)
    {
        _disabled = true;
        LastFailure = ex.Message;
        Close();
    }

    private void Close()
    {
        try
        {
            _writer?.Dispose();
        }
        catch (Exception ex) when (IsFileTrouble(ex))
        {
            // Closing a file that is already broken is not news.
        }

        _writer = null;
        FilePath = null;
    }

    /// <summary>
    /// The ways a file can misbehave, all of which mean "stop logging" and none of which mean
    /// "stop working". Anything else is a bug in here and is allowed out.
    /// </summary>
    private static bool IsFileTrouble(Exception ex) =>
        ex is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or ObjectDisposedException

            // A path the file system will not accept - an invalid character, a drive that is not
            // there. Caught rather than allowed out because the contract is that this never throws,
            // and the argument guards on Open already reject the cases that are programmer error.
            or ArgumentException
            or System.Security.SecurityException;

    private static string Label(EventSeverity severity) => severity switch
    {
        EventSeverity.Warn => "WARN ",
        EventSeverity.Error => "ERROR",
        _ => "INFO ",
    };

    /// <summary>
    /// One event is one line, so the file can be searched with a plain text tool. An exception's
    /// own stack is exempt and stays multi-line: that is what it is for.
    /// </summary>
    private static string OneLine(string message) =>
        message.ReplaceLineEndings(" | ");
}
