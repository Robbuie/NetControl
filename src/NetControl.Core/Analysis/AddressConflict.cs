using System.Net;

namespace NetControl.Core.Analysis;

/// <summary>
/// One IPv4 address claimed by more than one MAC within the detector's window.
/// </summary>
/// <param name="Address">The contested address.</param>
/// <param name="Claimants">Each MAC that claimed it, with when it was first and last seen doing so.</param>
/// <param name="IsOwnAddress">True when one of the claims is to this PC's own address.</param>
/// <param name="ProbedBy">MACs that probed for the address (RFC 5227) while somebody else held it.</param>
public sealed record AddressConflict(
    IPAddress Address,
    IReadOnlyList<AddressClaim> Claimants,
    bool IsOwnAddress,
    IReadOnlyList<MacAddress> ProbedBy)
{
    /// <summary>The last time anything was heard about this conflict.</summary>
    public DateTimeOffset LastSeenUtc => Claimants.Count == 0 ? DateTimeOffset.MinValue : Claimants.Max(c => c.LastUtc);

    /// <summary>"10.0.0.5: 00:1D:..., 00:0E:..." - for a list row.</summary>
    public string MacsText => string.Join(", ", Claimants.Select(c => c.Mac.ToString()));

    /// <summary>
    /// True when one claimant stopped being seen before the other started - an address that moved to
    /// a different device (a replaced module, a swapped card) rather than two devices fighting now.
    /// </summary>
    public bool LooksLikeAMove =>
        Claimants.Count == 2
        && (Claimants[0].LastUtc < Claimants[1].FirstUtc || Claimants[1].LastUtc < Claimants[0].FirstUtc);

    public NetworkFinding ToFinding()
    {
        if (IsOwnAddress)
        {
            return new NetworkFinding(
                FindingSeverity.Error,
                "Addresses",
                $"Another device is using this PC's own address {Address}",
                $"Claimed by {MacsText}.",
                "Change this PC's address (Set address... on the interface bar), or find and readdress the other device. "
                    + "Until then replies to this PC can go to the other device.");
        }

        if (LooksLikeAMove && ProbedBy.Count == 0)
        {
            return new NetworkFinding(
                FindingSeverity.Warn,
                "Addresses",
                $"{Address} moved from one device to another",
                $"First {Claimants[0].Mac}, then {Claimants[1].Mac}. A replaced module or a swapped card does this; "
                    + "so does a second device being plugged in with the same address after the first was unplugged.",
                "If a module was replaced, nothing to do - but update the plan's MAC for it. Otherwise find the second device.");
        }

        return new NetworkFinding(
            FindingSeverity.Error,
            "Addresses",
            $"Duplicate address {Address}",
            $"Claimed by {Claimants.Count} devices: {MacsText}"
                + (ProbedBy.Count > 0 ? $". Probed for by {string.Join(", ", ProbedBy)} while in use." : "."),
            "Two devices on one address work intermittently - each steals the traffic in turn. Unplug one and readdress it.");
    }
}
