namespace NetControl.Core.Persistence;

/// <summary>
/// How much attention an event needs. Stored as the lower-cased member name, so the column reads
/// as 'info' / 'warn' / 'error' exactly as ROADMAP.md specifies.
/// </summary>
public enum EventSeverity
{
    /// <summary>Something happened. Most of the commissioning record is this.</summary>
    Info,

    /// <summary>Worth a look, but the operation continued. A refused request lands here.</summary>
    Warn,

    /// <summary>The operation did not do what was asked.</summary>
    Error,
}
