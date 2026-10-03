namespace NetControl.Core.Capture;

/// <summary>
/// Whether raw capture can work on this machine, and if not, what to do about it - in the words the
/// Passive and PROFINET tabs show. Never an exception: a missing optional driver is a state of the
/// machine, not an error in the tool.
/// </summary>
public sealed record CaptureAvailability(bool IsAvailable, string Headline, string? Remediation = null, string? Version = null)
{
    public const string DownloadUrl = "https://npcap.com/#download";

    public static CaptureAvailability NotWindows { get; } = new(false, "Raw capture needs Windows.");

    public static CaptureAvailability NotInstalled { get; } = new(
        false,
        "Npcap is not installed, so this tab cannot see or send raw Ethernet frames.",
        "Install Npcap from npcap.com - free for use on your own machines; Wireshark's installer includes it - "
            + "and restart NetControl. Everything else in NetControl works without it.");
}
