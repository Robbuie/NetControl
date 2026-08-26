using NetControl.Core.Persistence;

namespace NetControl.Core.Diagnostics;

/// <summary>
/// The diagnostic log: what the <em>tool</em> did.
///
/// <para><b>Not the commissioning record.</b> <see cref="EventLog"/> is the account of what was
/// done to plant equipment, it lives in the project file, and it is append-only because somebody
/// may have to stand behind it. This is the other thing entirely - a rolling text file for working
/// out why the application misbehaved, thrown away on a schedule, and of interest to nobody but
/// whoever is fixing it.</para>
///
/// <para>Behind an interface for the ordinary reason: tests substitute <see cref="NullTraceLog"/>
/// and never touch a disk. The one member is <see cref="Write"/>; <c>Info</c>, <c>Warn</c> and
/// <c>Error</c> are extension methods in <see cref="TraceLogExtensions"/> rather than default
/// interface implementations, so that they can also be called on a concrete <see cref="TraceLog"/>
/// - which is how the application holds it, since somebody has to dispose it.</para>
/// </summary>
public interface ITraceLog
{
    /// <summary>
    /// Where the lines are going, for a message that has to tell somebody where to look. Null when
    /// nothing is being written.
    /// </summary>
    string? FilePath { get; }

    /// <summary>
    /// Writes one line. <b>Never throws</b> - a diagnostic log that can abort the operation it is
    /// describing is worse than no diagnostic log, and this one runs inside crash handlers.
    /// </summary>
    void Write(EventSeverity severity, string message, Exception? error = null);
}
