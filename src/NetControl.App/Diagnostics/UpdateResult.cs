namespace NetControl.App.Diagnostics;

/// <summary>
/// What an update check found, in a form a status line can show without deciding anything.
/// </summary>
/// <param name="Availability">Which of the four outcomes it was.</param>
/// <param name="LatestVersion">The published version, when one was read.</param>
/// <param name="DownloadUrl">
/// Where the published build is. Shown, and opened in a browser if somebody asks - never fetched
/// and never run. The installer is downloaded and started by a person who has decided to update a
/// plant laptop; a tool that did that for them is one plant IT is right to block.
/// </param>
/// <param name="Notes">One line from the manifest, if it carried one.</param>
/// <param name="Problem">Why the check did not complete, when it did not.</param>
/// <param name="Installer">
/// The setup exe attached to the release, for a copy that was installed. Null when the release
/// published none, or when the answer came from a mirrored manifest rather than a release - a
/// manifest names a version and a page, and knows nothing about files.
/// </param>
/// <param name="PortableExe">The loose exe attached to the release, for a copy that was not installed.</param>
public sealed record UpdateResult(
    UpdateAvailability Availability,
    string? LatestVersion = null,
    string? DownloadUrl = null,
    string? Notes = null,
    string? Problem = null,
    UpdateAsset? Installer = null,
    UpdateAsset? PortableExe = null)
{
    public static UpdateResult TurnedOff { get; } = new(UpdateAvailability.TurnedOff);

    public bool IsUpdateAvailable => Availability == UpdateAvailability.UpdateAvailable;

    /// <summary>
    /// The asset this copy of NetControl can actually apply, or null if the release published
    /// nothing it can use - in which case the releases page is the whole of what is on offer, and
    /// saying so is better than downloading a file that will not help.
    /// </summary>
    public UpdateAsset? AssetFor(InstallKind kind) =>
        kind == InstallKind.Installed ? Installer : PortableExe;

    /// <summary>
    /// What to put in the status bar, or null when there is nothing worth saying. Silence is the
    /// right answer for both "turned off" and "you are on the current build": neither is news, and
    /// a status bar that always has something in it is a status bar nobody reads.
    ///
    /// <para>A failed check does have text here, but the automatic check at startup does not show
    /// it - see <c>AppHost</c>. On a plant segment with no route out, failure is the expected
    /// answer every single launch, and a red line that is always there teaches people to ignore the
    /// status bar. It is shown when somebody used <b>Help &gt; Check for updates</b> and is
    /// therefore waiting for an answer.</para>
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
