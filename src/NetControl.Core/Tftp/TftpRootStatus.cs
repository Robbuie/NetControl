namespace NetControl.Core.Tftp;

/// <summary>
/// What a TFTP root folder can and cannot do, and what to do about it.
///
/// <para>Shaped like <c>FirewallStatus</c> deliberately: Core establishes the fact and words it,
/// the app grades it into a readiness line. The engine does not know a window exists.</para>
/// </summary>
/// <param name="Path">The folder as the caller gave it.</param>
/// <param name="Verdict">How it stands.</param>
/// <param name="Exists">Whether anything is at that path at all.</param>
/// <param name="FreeBytes">Free space on the volume, or null when it could not be established.</param>
/// <param name="Summary">One sentence, already user-facing, naming the cause rather than the symptom.</param>
/// <param name="Remediation">The next action, or null when there is nothing to do.</param>
public sealed record TftpRootStatus(
    string Path,
    TftpRootVerdict Verdict,
    bool Exists,
    long? FreeBytes,
    string Summary,
    string? Remediation)
{
    /// <summary>
    /// True only where we positively established that a file can be written. <see
    /// cref="TftpRootVerdict.Unknown"/> is not good news and must never be shown as green - the
    /// same rule the interface bar already holds for the firewall.
    /// </summary>
    public bool IsClear => Verdict is TftpRootVerdict.Writable;

    public static TftpRootStatus NotConfigured() => new(
        string.Empty,
        TftpRootVerdict.Unknown,
        false,
        null,
        "No TFTP root folder has been given, so nothing about the server's storage has been checked.",
        "Point the tool at the folder your TFTP server writes backups into to have it checked.");

    public static TftpRootStatus Unknown(string path, string reason) => new(
        path,
        TftpRootVerdict.Unknown,
        false,
        null,
        $"Could not check the TFTP root folder: {reason}",
        "Check the path by hand, and whether this account can reach it at all.");
}
