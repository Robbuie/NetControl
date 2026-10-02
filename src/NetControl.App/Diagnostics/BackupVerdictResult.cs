namespace NetControl.App.Diagnostics;

/// <summary>
/// What <see cref="BackupVerdict"/> concluded: one graded line per step of an image backup, and
/// the one sentence that says where it stopped.
/// </summary>
/// <param name="Steps">The four steps, in the order the controller takes them.</param>
/// <param name="Headline">The earliest step that is not known to have worked, and what it shows.</param>
/// <param name="Remediation">What to do about it, or null when there is nothing to do yet.</param>
/// <param name="Overall">The worst grade across the steps - grey until something has been seen.</param>
public sealed record BackupVerdictResult(
    IReadOnlyList<ReadinessCheck> Steps,
    string Headline,
    string? Remediation,
    ReadinessState Overall);
