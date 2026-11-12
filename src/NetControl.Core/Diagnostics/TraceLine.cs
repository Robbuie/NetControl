namespace NetControl.Core.Diagnostics;

/// <summary>
/// One line of the diagnostic log as the in-app viewer shows it.
/// </summary>
/// <param name="Utc">When it was written.</param>
/// <param name="Level">DEBUG, INFO, WARN or ERROR - the same label the file carries.</param>
/// <param name="Message">The one-line message.</param>
/// <param name="Detail">The exception, stack included, when one was logged with the line.</param>
public sealed record TraceLine(DateTimeOffset Utc, string Level, string Message, string? Detail = null)
{
    public const string Debug = "DEBUG";
    public const string Info = "INFO";
    public const string Warn = "WARN";
    public const string Error = "ERROR";

    /// <summary>Lower is chattier: DEBUG 0, INFO 1, WARN 2, ERROR 3.</summary>
    public int Rank => Level switch
    {
        Debug => 0,
        Warn => 2,
        Error => 3,
        _ => 1,
    };
}
