using NetControl.Core.Persistence;

namespace NetControl.Core.Diagnostics;

/// <summary>
/// A trace log that writes nothing.
///
/// <para>The default everywhere a log is optional, so that no caller has to null-check one. Tests
/// use it; so does anything constructed without a directory to write into.</para>
/// </summary>
public sealed class NullTraceLog : ITraceLog
{
    public static NullTraceLog Instance { get; } = new();

    private NullTraceLog()
    {
    }

    public void Write(EventSeverity severity, string message, Exception? error = null)
    {
        // Deliberately nothing.
    }
}
