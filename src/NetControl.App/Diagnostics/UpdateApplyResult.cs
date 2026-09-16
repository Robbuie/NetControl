namespace NetControl.App.Diagnostics;

/// <summary>
/// What happened when an update was applied, and - just as importantly - what the caller must do
/// next, which is different for the two ways of applying one.
/// </summary>
/// <param name="Outcome">Which of the three it was.</param>
/// <param name="Problem">Why nothing was replaced, when nothing was.</param>
public sealed record UpdateApplyResult(UpdateApplyOutcome Outcome, string? Problem = null)
{
    /// <summary>
    /// Setup is running and will close this process itself through the Restart Manager, then start
    /// the new build. <b>The caller must not exit</b> - a process that has already gone is not one
    /// the installer can put back.
    /// </summary>
    public static UpdateApplyResult InstallerRunning { get; } = new(UpdateApplyOutcome.InstallerRunning);

    /// <summary>
    /// The executable has been replaced and the new one is starting. The caller shuts down now, and
    /// for a moment there are two NetControls: the outgoing one, and the new one coming up on an
    /// empty in-memory project.
    /// </summary>
    public static UpdateApplyResult Restarting { get; } = new(UpdateApplyOutcome.Restarting);

    /// <summary>
    /// Nothing was replaced, and the message says both why and where the verified download is - so
    /// a failure here is a manual install rather than a dead end.
    /// </summary>
    public static UpdateApplyResult Failed(string problem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(problem);

        return new UpdateApplyResult(UpdateApplyOutcome.Failed, problem);
    }
}
