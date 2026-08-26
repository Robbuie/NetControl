using NetControl.Core.Persistence;

namespace NetControl.Core.Diagnostics;

/// <summary>
/// The three ordinary ways to write a line.
///
/// <para>Extension methods rather than default interface implementations, deliberately. A default
/// implementation is only reachable through the interface, and the application holds its log as a
/// concrete <see cref="TraceLog"/> because somebody has to dispose it - so <c>log.Info(...)</c>
/// would not compile at exactly the call sites that matter most.</para>
/// </summary>
public static class TraceLogExtensions
{
    public static void Info(this ITraceLog log, string message)
    {
        ArgumentNullException.ThrowIfNull(log);
        log.Write(EventSeverity.Info, message);
    }

    public static void Warn(this ITraceLog log, string message, Exception? error = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        log.Write(EventSeverity.Warn, message, error);
    }

    public static void Error(this ITraceLog log, string message, Exception? error = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        log.Write(EventSeverity.Error, message, error);
    }
}
