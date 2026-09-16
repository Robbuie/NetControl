namespace NetControl.App.Diagnostics;

/// <summary>
/// How an update check ended.
///
/// <para>The distinction between "turned off" and "could not be checked" is the one that earns its
/// keep. The first is a site that has deliberately decided this tool makes no outbound requests,
/// and should be silent; the second means a check was attempted and did not get an answer, and
/// saying nothing about that is how a site ends up believing it is on the latest build for a year.
/// </para>
/// </summary>
public enum UpdateAvailability
{
    /// <summary>
    /// Nothing was contacted, because <c>checkForUpdates</c> is false in the settings file. The
    /// only state in which this tool makes no network connection at all.
    /// </summary>
    TurnedOff,

    /// <summary>Checked, and this is the newest published build.</summary>
    Current,

    /// <summary>Checked, and a newer build has been published.</summary>
    UpdateAvailable,

    /// <summary>The check was attempted and did not complete. See the reason.</summary>
    Failed,
}
