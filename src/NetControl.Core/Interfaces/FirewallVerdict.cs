namespace NetControl.Core.Interfaces;

/// <summary>
/// "Nothing is arriving" has four common causes: the wrong adapter, a port conflict, the
/// firewall, and a device that simply is not asking. The tool's job is to rule out the first
/// three without the user guessing. This is the third.
/// </summary>
public enum FirewallVerdict
{
    /// <summary>The firewall could not be queried. Say so; do not imply it is fine.</summary>
    Unknown = 0,

    /// <summary>The firewall is off for every active profile, so no rule is needed.</summary>
    NotEnforced = 1,

    /// <summary>An enabled inbound allow rule covers this executable on this port.</summary>
    Allowed = 2,

    /// <summary>
    /// No rule matches. Windows blocks unsolicited inbound traffic by default, so requests will
    /// most likely be dropped — though the first bind may raise the standard Windows prompt.
    /// </summary>
    NoRule = 3,

    /// <summary>An explicit block rule matches. Traffic will be dropped, and no prompt will appear.</summary>
    Blocked = 4,
}
