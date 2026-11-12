namespace NetControl.Core.Analysis;

/// <summary>How much an analysis finding matters. Ordered so a plain sort puts the worst first.</summary>
public enum FindingSeverity
{
    /// <summary>Checked, and fine. Shown so a clean result says what it checked.</summary>
    Ok = 0,

    /// <summary>Worth knowing; not a fault.</summary>
    Info = 1,

    /// <summary>Probably a problem, or the cause of one.</summary>
    Warn = 2,

    /// <summary>Something is wrong and will stop things working.</summary>
    Error = 3,
}
