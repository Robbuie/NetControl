namespace NetControl.App.Diagnostics;

/// <summary>
/// What an update check found, in a form a status line can show without deciding anything.
/// </summary>
/// <param name="Availability">Which of the four outcomes it was.</param>
/// <param name="LatestVersion">The published version, when one was read.</param>
/// <param name="DownloadUrl">
/// Where the published build is. Shown, never fetched: this tool does not download or run
/// executables, and one that did would be one plant IT is right to block.
/// </param>
/// <param name="Notes">One line from the manifest, if it carried one.</param>
/// <param name="Problem">Why the check did not complete, when it did not.</param>
public sealed record UpdateResult(
    UpdateAvailability Availability,
    string? LatestVersion = null,
    string? DownloadUrl = null,
    string? Notes = null,
    string? Problem = null)
{
    public static UpdateResult NotConfigured { get; } = new(UpdateAvailability.NotConfigured);

    public bool IsUpdateAvailable => Availability == UpdateAvailability.UpdateAvailable;

    /// <summary>
    /// What to put in the status bar, or null when there is nothing worth saying. Silence is the
    /// right answer for both "not configured" and "you are on the current build": neither is news,
    /// and a status bar that always has something in it is a status bar nobody reads.
    /// </summary>
    public string? StatusText => Availability switch
    {
        UpdateAvailability.UpdateAvailable =>
            $"Version {LatestVersion} is available"
                + (DownloadUrl is null ? "." : $" - {DownloadUrl}")
                + (Notes is null ? string.Empty : $" ({Notes})"),

        UpdateAvailability.Failed => $"Could not check for updates: {Problem}",

        _ => null,
    };
}
