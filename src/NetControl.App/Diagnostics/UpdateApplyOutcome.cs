namespace NetControl.App.Diagnostics;

/// <summary>
/// How applying an update ended.
///
/// <para>The two successes are kept apart because they place opposite obligations on the caller:
/// after <see cref="InstallerRunning"/> the application must stay alive to be closed by setup, and
/// after <see cref="Restarting"/> it must shut down so the copy that has already started can have
/// the screen. A single "it worked" would be right about half the time.</para>
/// </summary>
public enum UpdateApplyOutcome
{
    /// <summary>Nothing was replaced. See the problem, which names where the download is.</summary>
    Failed,

    /// <summary>Setup is running; it closes this process and starts the new build. Do not exit.</summary>
    InstallerRunning,

    /// <summary>The exe was swapped and the new one is starting. Shut down.</summary>
    Restarting,
}
