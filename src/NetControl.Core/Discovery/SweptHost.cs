using System.Net;
using NetControl.Core.Reachability;

namespace NetControl.Core.Discovery;

/// <summary>
/// One address a subnet sweep found something at.
///
/// <para>Two kinds of evidence, kept apart because they mean different things to somebody standing
/// at a panel: an echo reply says the device is there and answering, and an ARP entry with no echo
/// reply says it is there and ignoring ping - which plenty of drives and I/O adapters do on purpose.
/// Both are a device holding that address; neither says what the device is. That is the
/// EtherNet/IP identity scan's job, and the app joins the two by address.</para>
/// </summary>
public sealed record SweptHost
{
    public required IPAddress Address { get; init; }

    /// <summary>The echo reply, or the silence, for this address. Null when it was never pinged.</summary>
    public PingOutcome? Ping { get; init; }

    /// <summary>Whether this PC's ARP cache held a usable entry for the address on the swept adapter.</summary>
    public bool InArp { get; init; }

    public MacAddress Mac { get; init; } = MacAddress.Empty;

    public MacSource MacSource { get; init; } = MacSource.None;

    /// <summary>From the IEEE registry against <see cref="Mac"/>, resolved once and carried as text.</summary>
    public string? Vendor { get; init; }

    public bool AnsweredPing => Ping?.Replied == true;

    /// <summary>
    /// Whether this can become a plan row: keyed on a MAC, learned on the adapter the sweep went out
    /// of - the same rule <see cref="DiscoveredDevice.CanBePlanned"/> keeps.
    /// </summary>
    public bool CanBePlanned => !Mac.IsEmpty && MacSource == MacSource.Arp;

    /// <summary>Why it cannot, in words that name the next action. Null when it can.</summary>
    public string? PlanningObstacle => MacSource switch
    {
        MacSource.Arp => null,

        MacSource.NotChecked =>
            "The ARP table cannot be read on this machine, so there is no hardware address to plan against.",

        MacSource.Routed =>
            $"{Address} is behind a router, so its MAC cannot be seen from this PC and it cannot be planned from here. "
                + "Plug into its own network and scan again, or read the MAC off the device's label.",

        MacSource.ArpOnAnotherAdapter =>
            $"{Address} is in the ARP cache, but a different adapter learned it - so that hardware address "
                + "probably belongs to something on another segment.",

        _ =>
            $"{Address} answered ping but is not in this machine's ARP cache, so its MAC is unknown. "
                + "Sweep again, or read the address off the device's label.",
    };

    /// <summary>How it was found, short enough for a grid cell.</summary>
    public string HowFound => AnsweredPing ? (Ping!.Text) : "ARP only";

    /// <summary>One sentence for a tooltip or a log line.</summary>
    public string Sentence => AnsweredPing
        ? Ping!.Sentence
        : $"{Address} did not answer ping, but answered ARP - it is there and set to ignore ping, which many "
            + "drives and I/O adapters are.";

    public override string ToString() =>
        $"{Address}"
        + (Mac.IsEmpty ? string.Empty : $" {Mac}")
        + (Vendor is null ? string.Empty : $" - {Vendor}")
        + $" ({HowFound})";
}
