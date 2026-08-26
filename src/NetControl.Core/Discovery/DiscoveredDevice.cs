using System.Net;
using NetControl.Core.Enip;

namespace NetControl.Core.Discovery;

/// <summary>
/// One device a scan found: what it said about itself, plus the two things the identity object
/// does not carry - its hardware address, and who made it.
/// </summary>
public sealed record DiscoveredDevice
{
    public required ListIdentityReply Identity { get; init; }

    /// <summary>Resolved from the ARP cache, or empty. <see cref="MacSource"/> says which.</summary>
    public MacAddress Mac { get; init; } = MacAddress.Empty;

    public MacSource MacSource { get; init; } = MacSource.None;

    /// <summary>
    /// From the IEEE registry, against the MAC - not from the device's ODVA vendor id, which is a
    /// different numbering scheme entirely. Null when there is no MAC or the prefix is unknown.
    /// </summary>
    public string? Vendor { get; init; }

    /// <summary>
    /// Another device answered on this same address. Two devices on one address is the fault a
    /// scan is uniquely able to find, and it is why replies are never collapsed by address alone.
    /// </summary>
    public bool AddressIsContested { get; init; }

    /// <summary>The address the device claims. This is the one a plan or a conflict is about.</summary>
    public IPAddress Address => Identity.Address;

    /// <summary>
    /// Where the reply actually came from. Differs from <see cref="Address"/> only when something
    /// is odd - a router in the path, or a device whose configuration and its wiring disagree.
    /// </summary>
    public IPAddress SenderAddress => Identity.SenderAddress;

    public string ProductName => Identity.ProductName;

    /// <summary>
    /// Whether this result can become a plan row. The plan is keyed on MAC, so without one there
    /// is nothing to key on - the device is real, it is just not something BOOTP can be told about.
    /// </summary>
    public bool CanBePlanned => !Mac.IsEmpty && MacSource == MacSource.Arp;

    /// <summary>
    /// Why it cannot, in words that name the next action. Null when it can.
    ///
    /// <para>The wording lives here rather than in a view model for the same reason
    /// <c>DeviceRecord.TryToAssignment</c>'s does: the grid, the scan list and any later report
    /// must not be able to explain the same refusal differently.</para>
    /// </summary>
    public string? PlanningObstacle => MacSource switch
    {
        MacSource.Arp => null,

        MacSource.NotChecked =>
            "The ARP table cannot be read on this machine, so there is no hardware address to plan against.",

        MacSource.ArpOnAnotherAdapter =>
            $"{SenderAddress} is in the ARP cache, but a different adapter learned it - so that hardware "
                + "address probably belongs to something else on another segment. Scan from the adapter the "
                + "device is actually on.",

        _ =>
            $"{SenderAddress} answered the scan but is not in this machine's ARP cache, so its MAC is unknown. "
                + "Ping it once and scan again, or read the address off the module's label.",
    };

    /// <summary>
    /// The one line the UI and the event log both use. Product name first, because on a panel that
    /// is what somebody is looking for.
    /// </summary>
    public override string ToString() =>
        $"{Address} {(ProductName.Length == 0 ? "(unnamed device)" : ProductName)}"
        + (Mac.IsEmpty ? string.Empty : $" {Mac}")
        + (Vendor is null ? string.Empty : $" - {Vendor}");
}
