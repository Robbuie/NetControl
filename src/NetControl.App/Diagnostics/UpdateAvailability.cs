namespace NetControl.App.Diagnostics;

/// <summary>
/// How an update check ended.
///
/// <para>The distinction between "not configured" and "could not be checked" is the one that
/// earns its keep. The first is the ordinary state on a plant laptop and should be silent; the
/// second means somebody asked for a check and did not get one, and saying nothing about that is
/// how a site ends up believing it is on the latest build for a year.</para>
/// </summary>
public enum UpdateAvailability
{
    /// <summary>No manifest URL is set, so nothing was contacted. The default everywhere.</summary>
    NotConfigured,

    /// <summary>Checked, and this is the newest published build.</summary>
    Current,

    /// <summary>Checked, and a newer build has been published.</summary>
    UpdateAvailable,

    /// <summary>The check was asked for and did not complete. See the reason.</summary>
    Failed,
}
