using System.Globalization;
using System.Text;

namespace NetControl.Spike.TftpWatch;

/// <summary>
/// Mirrors everything printed to a file, so a bench session leaves something behind.
///
/// <para>The reason this exists is not tidiness. The deliverable of BENCH.md Run 6b is three
/// strings - the filename, the mode, the options - printed once, into a console window that is
/// ended with Ctrl+C, on a laptop in a plant, by somebody who has just failed a backup on purpose
/// and has a robot to put back. A scrollback buffer is not a record.</para>
///
/// <para><b>Every line is flushed</b>, for the same reason <c>TraceLog</c> flushes: the most
/// valuable line in the file is the last one before something went wrong. And it appends rather
/// than truncating, so a second run at the same path adds to the session rather than erasing the
/// first one.</para>
///
/// <para>This is <i>not</i> the commissioning record. It is a copy of what a person saw. The record
/// is the project file's append-only event log - <c>--project</c> - and the two are deliberately
/// different things: one is readable by whoever was standing there, the other is queryable
/// afterwards and cannot be edited.</para>
/// </summary>
internal sealed class Transcript : TextWriter
{
    private readonly TextWriter _console;
    private readonly TextWriter _consoleError;
    private readonly StreamWriter _file;

    private bool _disposed;

    private Transcript(string path, TextWriter console, TextWriter consoleError, StreamWriter file)
    {
        Path = path;
        _console = console;
        _consoleError = consoleError;
        _file = file;
    }

    /// <summary>Where it is being written, in full, so the closing line can name it.</summary>
    public string Path { get; }

    public override Encoding Encoding => _console.Encoding;

    /// <summary>
    /// Opens the file and takes over <see cref="Console.Out"/> and <see cref="Console.Error"/>.
    ///
    /// <para>Returns false rather than throwing. A transcript that cannot be opened must not stop
    /// the watch: the person may already have stopped the plant's TFTP server to run it, and losing
    /// the observation to a read-only folder would be the worst possible trade.</para>
    /// </summary>
    public static bool TryOpen(string path, out Transcript? transcript, out string? problem)
    {
        transcript = null;
        problem = null;

        try
        {
            string full = System.IO.Path.GetFullPath(path);
            string? directory = System.IO.Path.GetDirectoryName(full);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var file = new StreamWriter(full, append: true) { AutoFlush = true };

            WriteHeader(file, full);

            var opened = new Transcript(full, Console.Out, Console.Error, file);
            Console.SetOut(opened);
            Console.SetError(opened);

            transcript = opened;
            return true;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            problem = ex.Message;
            return false;
        }
    }

    public override void Write(char value)
    {
        _console.Write(value);
        _file.Write(value);
    }

    public override void Write(string? value)
    {
        _console.Write(value);
        _file.Write(value);
    }

    public override void WriteLine(string? value)
    {
        _console.WriteLine(value);
        _file.WriteLine(value);
    }

    public override void WriteLine()
    {
        _console.WriteLine();
        _file.WriteLine();
    }

    public override void Flush()
    {
        _console.Flush();
        _file.Flush();
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            _disposed = true;

            // Put the real console back before the file goes, so anything printed during shutdown
            // still reaches a person even if it no longer reaches the file.
            Console.SetOut(_console);
            Console.SetError(_consoleError);

            _file.WriteLine();
            _file.WriteLine($"--- ended {Stamp()} ---");
            _file.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Enough for the file to stand alone once it has been emailed to somebody. Which machine,
    /// which account and which switches were used are all part of interpreting what follows -
    /// "writable by me" is not "writable by the service account" is the same lesson.
    /// </summary>
    private static void WriteHeader(TextWriter file, string full)
    {
        file.WriteLine();
        file.WriteLine($"=== tftp-spike transcript, started {Stamp()} ===");
        file.WriteLine($"    machine   : {Environment.MachineName}");
        file.WriteLine($"    user      : {Environment.UserName}");
        file.WriteLine($"    executable: {Environment.ProcessPath ?? "(unknown)"}");
        file.WriteLine($"    arguments : {string.Join(' ', Environment.GetCommandLineArgs().Skip(1))}");
        file.WriteLine($"    file      : {full}");
        file.WriteLine();
    }

    private static string Stamp() =>
        DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
}
