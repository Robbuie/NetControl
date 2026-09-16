namespace NetControl.App.Diagnostics;

/// <summary>
/// How this copy of NetControl got onto the machine, which decides how it can replace itself.
///
/// <para>The two are genuinely different operations, not two settings of one operation, which is
/// why this is an enum rather than a boolean on the applier: one runs an installer and lets it put
/// the new files down, the other renames a running executable out of the way. Getting them
/// backwards does nothing useful in either direction - an installer run against a folder on a USB
/// stick installs a second copy somewhere else, and a file swap against an installed copy leaves
/// Add/Remove Programs describing a version that is no longer there.</para>
/// </summary>
public enum InstallKind
{
    /// <summary>
    /// A loose self-contained exe, run from wherever it was copied to. Updated by downloading the
    /// new exe and swapping it in - see <c>UpdateApplier</c>.
    /// </summary>
    Portable,

    /// <summary>
    /// Installed by <c>NetControl-Setup-*.exe</c>, so Windows has a record of it and there is
    /// something to upgrade in place. Updated by running the new installer silently.
    /// </summary>
    Installed,
}
