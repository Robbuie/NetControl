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
/// <para><b>Every line is flushed.</b> The single most valuable entry in this file is the last one
/// before a crash, and a buffered writer is precisely the thing that loses it. This costs
/// throughput that a tool writing a few lines a minute does not have to care about.</para>
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
        if (_disabled || _disposed)
        {
            return;
        }

        lock (_gate)
        {
            if (_disabled || _disposed)
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

                _writer.Flush();
            }
            catch (Exception ex) when (IsFileTrouble(ex))
            {
                Disable(ex);
            }
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
