namespace NetControl.Core.Interfaces;

/// <summary>
/// How much an existing owner of the port actually matters. The distinction is the point: a
/// wildcard bind makes delivery ambiguous for every adapter, while an address-specific bind on
/// a subnet you do not care about is usually harmless. Reporting both as "port in use" is what
/// trains people to ignore the warning.
/// </summary>
public enum PortConflictSeverity
{
    /// <summary>Nobody else holds the port.</summary>
    None = 0,

    /// <summary>Somebody holds it, but bound to a specific address that is unlikely to matter.</summary>
    Advisory = 1,

    /// <summary>
    /// A wildcard bind, or another copy of this tool. Windows will let us bind alongside it and
    /// then deliver requests to whichever socket it feels like. This is the classic
    /// "tool runs, sees nothing" case and it must not be presented as a footnote.
    /// </summary>
    Serious = 2,
}
