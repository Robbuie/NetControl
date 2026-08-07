namespace NetControl.Core.Interfaces;

/// <summary>What the firewall will do to inbound traffic on a port, and what to do about it.</summary>
public sealed record FirewallStatus(
    FirewallVerdict Verdict,
    int Port,
    IReadOnlyList<string> AllowRules,
    IReadOnlyList<string> BlockRules,
    string Summary,
    string? Remediation)
{
    /// <summary>
    /// True only when we positively established that traffic gets through. <see
    /// cref="FirewallVerdict.Unknown"/> is not good news and must not be shown as green.
    /// </summary>
    public bool IsClear => Verdict is FirewallVerdict.Allowed or FirewallVerdict.NotEnforced;

    public static FirewallStatus Unknown(int port, string reason) => new(
        FirewallVerdict.Unknown,
        port,
        [],
        [],
        $"Could not read the Windows Firewall configuration: {reason}",
        "If nothing arrives, check inbound UDP/" + port.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + " manually in Windows Defender Firewall.");
}
