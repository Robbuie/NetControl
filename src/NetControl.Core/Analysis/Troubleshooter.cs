using System.Globalization;
using System.Net;
using NetControl.Core.Interfaces;
using NetControl.Core.Reachability;

namespace NetControl.Core.Analysis;

/// <summary>
/// Turns everything that can be learned about the path from this PC to the devices into a ranked
/// list: what is wrong, the evidence, and the next step.
///
/// <para>The order of the checks is the order a person would walk the path - this PC's adapter,
/// its cable, what else on this PC is in the way, then addresses, then the devices, then the
/// traffic - so a list read top to bottom is also a walk from the laptop to the panel. Findings are
/// then sorted worst first, stably, which keeps that order within each grade.</para>
///
/// <para>A pure function. <see cref="TroubleshootRunner"/> gathers the facts; this judges them.</para>
/// </summary>
public static class Troubleshooter
{
    /// <summary>Broadcast and multicast frames a second, received by this PC, that earn a warning.</summary>
    public const double NonUnicastWarnPerSecond = 500;

    /// <summary>... and that is called a storm.</summary>
    public const double NonUnicastStormPerSecond = 2000;

    public static IReadOnlyList<NetworkFinding> Assess(TroubleshootInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var findings = new List<NetworkFinding>();

        Adapter(inputs, findings);

        if (inputs.Adapter is { } nic && nic.IsUp)
        {
            OtherAdapters(nic, inputs, findings);
            Counters(inputs, findings);
            OwnAddress(inputs, findings);
            PlanSubnets(nic, inputs, findings);
        }

        LocalSoftware(inputs, findings);
        Conflicts(inputs, findings);
        ArpAgainstPlan(inputs, findings);
        Active(inputs, findings);
        Traffic(inputs, findings);

        if (inputs.UnplannedAskers > 0)
        {
            findings.Add(new NetworkFinding(
                FindingSeverity.Info,
                "Devices",
                F($"{inputs.UnplannedAskers} device(s) are asking for an address and are not in the plan"),
                "They are in Live requests, marked as not planned. Serve mode never answers them.",
                "Double-click one in Live requests to plan it, or Assign IP to give it an address in one step."));
        }

        // Stable sort: worst first, path order within a grade.
        List<NetworkFinding> sorted = [.. findings.Select((f, i) => (f, i)).OrderByDescending(x => x.f.Severity).ThenBy(x => x.i).Select(x => x.f)];

        // A clean run leads with the verdict, so the first line read is the answer.
        if (!sorted.Any(f => f.Severity >= FindingSeverity.Warn))
        {
            sorted.Insert(0, new NetworkFinding(
                FindingSeverity.Ok,
                "Summary",
                "No problems found",
                "Everything checked below looks right. If a device still misbehaves, run Diagnose on it (plan row > More) "
                    + "for its own port counters, or start the ping monitor on it to catch drop-outs.",
                null));
        }

        return sorted;
    }

    private static void Adapter(TroubleshootInputs inputs, List<NetworkFinding> findings)
    {
        if (inputs.Adapter is not { } nic)
        {
            findings.Add(new NetworkFinding(
                FindingSeverity.Error,
                "This PC",
                "No adapter is selected",
                "Everything this tool sends or listens for goes through the adapter chosen at the top of the window.",
                "Pick the Ethernet port the panel is plugged into. Virtual adapters (VMware, Hyper-V, VPN) are listed last."));
            return;
        }

        string name = $"[{nic.Index}] {nic.Name}";

        if (!nic.IsUp)
        {
            findings.Add(new NetworkFinding(
                FindingSeverity.Error,
                "Cable",
                $"{name} has no link",
                $"Windows reports it as {nic.Status}.",
                "Check the cable is in, the switch port is lit, and the device or switch at the far end has power. "
                    + "A docking station's port only works while the laptop is docked."));
            return;
        }

        if (nic.IPv4 is null)
        {
            findings.Add(new NetworkFinding(
                FindingSeverity.Error,
                "This PC",
                $"{name} has no IPv4 address",
                "It has a link but no address, so it cannot talk to anything.",
                "Use Set address... on the interface bar to give it one on the panel's subnet."));
        }
        else if (nic.IsApipa)
        {
            findings.Add(new NetworkFinding(
                FindingSeverity.Error,
                "This PC",
                $"{name} only has a self-assigned address ({nic.IPv4})",
                "It asked for an address and nothing answered - normal on a controls network with no DHCP server.",
                "Use Set address... on the interface bar to give it a static address on the panel's subnet."));
        }
        else
        {
            findings.Add(new NetworkFinding(
                FindingSeverity.Ok,
                "This PC",
                F($"{name} is up at {nic.IPv4}/{nic.PrefixLength}"),
                LinkSpeedText(nic.SpeedBps),
                null));
        }

        if (nic.SpeedBps > 0 && nic.SpeedBps <= 10_000_000)
        {
            findings.Add(new NetworkFinding(
                FindingSeverity.Warn,
                "Cable",
                $"{name} linked at 10 Mb/s",
                "A modern port linking at 10 Mb/s usually means a damaged cable (only two pairs working), a bad "
                    + "connector, or one end forced to a fixed speed while the other auto-negotiates - a duplex mismatch.",
                "Swap the patch lead. If it persists, check neither end is set to a fixed speed and duplex."));
        }

        if (nic.IsLikelyVirtual)
        {
            findings.Add(new NetworkFinding(
                FindingSeverity.Warn,
                "This PC",
                $"{name} looks like a virtual adapter",
                $"{nic.Description}. Virtual adapters (VMware, Hyper-V, VPN) do not reach the panel.",
                "Select the physical Ethernet port instead."));
        }
    }

    private static void OtherAdapters(NicInfo nic, TroubleshootInputs inputs, List<NetworkFinding> findings)
    {
        if (!Ipv4Subnet.TryCreate(nic.IPv4, nic.Mask, out Ipv4Subnet mine))
        {
            return;
        }

        foreach (NicInfo other in inputs.AllAdapters)
        {
            if (other.Index == nic.Index || !other.IsUp || other.IPv4 is null)
            {
                continue;
            }

            if (Ipv4Subnet.TryCreate(other.IPv4, other.Mask, out Ipv4Subnet theirs)
                && (mine.Contains(other.IPv4) || theirs.Contains(nic.IPv4!)))
            {
                findings.Add(new NetworkFinding(
                    FindingSeverity.Warn,
                    "This PC",
                    $"[{other.Index}] {other.Name} is on the same subnet",
                    $"It holds {other.IPv4} while [{nic.Index}] {nic.Name} holds {nic.IPv4}. Windows sends traffic for that "
                        + "subnet out of whichever it prefers, which may not be the port the panel is on.",
                    "Disconnect or disable the other adapter (often Wi-Fi on a plant network, or a second dock port) while working."));
            }
        }
    }

    private static void Counters(TroubleshootInputs inputs, List<NetworkFinding> findings)
    {
        if (inputs.CountersBefore is not { } before || inputs.CountersAfter is not { } after || after.Index != before.Index)
        {
            return;
        }

        double seconds = (after.ReadUtc - before.ReadUtc).TotalSeconds;
        if (seconds <= 0)
        {
            return;
        }

        long errors = (after.ReceivedWithErrors - before.ReceivedWithErrors) + (after.SentWithErrors - before.SentWithErrors);
        long discards = after.ReceivedDiscarded - before.ReceivedDiscarded;
        double nonUnicast = (after.NonUnicastReceived - before.NonUnicastReceived) / seconds;

        if (errors < 0 || discards < 0)
        {
            findings.Add(new NetworkFinding(
                FindingSeverity.Warn,
                "Cable",
                "This PC's adapter reset while it was being watched",
                "Its counters went backwards - the link dropped and came back.",
                "A link that drops by itself is a cable, a connector or the switch port. Watch the port's light while wiggling the cable."));
            return;
        }

        if (errors > 0)
        {
            findings.Add(new NetworkFinding(
                FindingSeverity.Error,
                "Cable",
                F($"This PC's port counted {errors} error(s) in {seconds:0} s"),
                "Frames are arriving or leaving damaged. That is the physical layer on this PC's side: the patch lead, "
                    + "the connector, the dock, or a duplex mismatch with the switch port.",
                "Swap the patch lead first, then try another switch port. Errors on this PC's own port are never the devices' fault."));
        }
        else
        {
            findings.Add(new NetworkFinding(
                FindingSeverity.Ok,
                "Cable",
                F($"No errors on this PC's port in {seconds:0} s"),
                F($"{after.ReceivedWithErrors:N0} errors in total since the adapter came up; none new while watching."),
                null));
        }

        if (discards > 0)
        {
            findings.Add(new NetworkFinding(
                FindingSeverity.Info,
                "This PC",
                F($"This PC discarded {discards} received frame(s) in {seconds:0} s"),
                "Usually traffic for a protocol nothing on this PC uses - harmless - or a receive buffer overrun under a flood.",
                null));
        }

        if (nonUnicast >= NonUnicastStormPerSecond)
        {
            findings.Add(new NetworkFinding(
                FindingSeverity.Error,
                "Traffic",
                F($"This PC is receiving {nonUnicast:0} broadcast/multicast frames a second"),
                "That is a storm: every device on the segment is having to read all of it, and small controllers fall over under far less.",
                "Look for a loop - a cable patched between two switch ports. The Passive tab names the top talkers if Npcap is installed."));
        }
        else if (nonUnicast >= NonUnicastWarnPerSecond)
        {
            findings.Add(new NetworkFinding(
                FindingSeverity.Warn,
                "Traffic",
                F($"This PC is receiving {nonUnicast:0} broadcast/multicast frames a second"),
                "Higher than a healthy controls segment. Multicast EtherNet/IP I/O can account for it on a busy line.",
                "Listen on the Passive tab to see who is sending it."));
        }
    }

    private static void OwnAddress(TroubleshootInputs inputs, List<NetworkFinding> findings)
    {
        if (inputs.OwnAddressDuplicate == true && inputs.Adapter is { } nic)
        {
            findings.Add(new NetworkFinding(
                FindingSeverity.Error,
                "Addresses",
                $"Windows says this PC's address {nic.IPv4} is already in use",
                "Duplicate address detection found another device answering for it. Windows will not use the address until it is free.",
                "Pick a different address with Set address..., or find the device that has it."));
        }
    }

    private static void PlanSubnets(NicInfo nic, TroubleshootInputs inputs, List<NetworkFinding> findings)
    {
        List<PlannedAddress> addressed = [.. inputs.Plan.Where(p => p.Address is not null)];
        if (addressed.Count == 0)
        {
            return;
        }

        List<Ipv4Subnet> mine = [.. nic.PerAddress()
            .Select(a => Ipv4Subnet.TryCreate(a.IPv4, a.Mask, out Ipv4Subnet s) ? (Ipv4Subnet?)s : null)
            .Where(s => s is not null)
            .Select(s => s!.Value)];

        int reachable = addressed.Count(p => mine.Any(s => s.Contains(p.Address!)));

        if (reachable == 0)
        {
            string example = addressed[0].Address!.ToString();
            findings.Add(new NetworkFinding(
                FindingSeverity.Warn,
                "Addresses",
                "This PC is not on any of the plan's subnets",
                F($"The plan has {addressed.Count} addressed device(s) - {example}, for one - and [{nic.Index}] {nic.Name} is at ")
                    + F($"{nic.IPv4}/{nic.PrefixLength}. Set static, Check, ping and the readback cannot reach them from here."),
                "Use Set address... on the interface bar - its profiles include each plan subnet."));
        }
        else if (reachable < addressed.Count)
        {
            findings.Add(new NetworkFinding(
                FindingSeverity.Info,
                "Addresses",
                F($"{addressed.Count - reachable} of {addressed.Count} planned address(es) are on subnets this PC is not on"),
                "Normal for a plan that spans several networks; those devices can only be reached from their own subnet.",
                "Add a second address to the adapter (Set address... > Add) to work on two subnets at once."));
        }
    }

    private static void LocalSoftware(TroubleshootInputs inputs, List<NetworkFinding> findings)
    {
        if (inputs.DhcpPort is { } port && !port.IsClear)
        {
            findings.Add(new NetworkFinding(
                port.Severity == PortConflictSeverity.Serious ? FindingSeverity.Error : FindingSeverity.Warn,
                "This PC",
                "Another program holds UDP/67",
                port.Summary,
                port.Remediation));
        }

        if (inputs.Firewall is { } firewall && !firewall.IsClear)
        {
            findings.Add(new NetworkFinding(
                FindingSeverity.Warn,
                "This PC",
                "Windows Firewall may block BOOTP/DHCP requests",
                firewall.Summary,
                firewall.Remediation));
        }
    }

    private static void Conflicts(TroubleshootInputs inputs, List<NetworkFinding> findings)
    {
        findings.AddRange(inputs.Conflicts.Select(c => c.ToFinding()));
    }

    private static void ArpAgainstPlan(TroubleshootInputs inputs, List<NetworkFinding> findings)
    {
        int adapter = inputs.Adapter?.Index ?? -1;
        var byAddress = new Dictionary<IPAddress, ArpEntry>();

        foreach (ArpEntry entry in inputs.Arp)
        {
            if (entry.IsUsable && entry.InterfaceIndex == adapter)
            {
                byAddress[entry.Address] = entry;
            }
        }

        foreach (PlannedAddress device in inputs.Plan)
        {
            if (device.Address is null || device.Mac is not { } planned || planned.IsEmpty
                || !byAddress.TryGetValue(device.Address, out ArpEntry? entry) || entry.Mac == planned)
            {
                continue;
            }

            findings.Add(new NetworkFinding(
                FindingSeverity.Error,
                "Addresses",
                $"{device.Address} is held by a different device than the plan says",
                $"The plan has {device.Name} at {device.Address} with MAC {planned}; this PC last reached that address at {entry.Mac}.",
                "Either the module was replaced (update the plan's MAC) or another device is sitting on the address - unplug it and readdress it."));
        }
    }

    private static void Active(TroubleshootInputs inputs, List<NetworkFinding> findings)
    {
        if (!inputs.ActiveChecksRan)
        {
            findings.Add(new NetworkFinding(
                FindingSeverity.Info,
                "Devices",
                inputs.ListenOnly ? "Ping checks skipped - listen-only mode is on" : "Ping checks not run",
                "Without them the gateway and the planned devices are not checked for an answer.",
                inputs.ListenOnly ? "Turn Listen only off and run again to include them." : "Tick \"Include ping checks\" and run again."));
            return;
        }

        if (inputs.GatewayPing is { } gateway)
        {
            findings.Add(gateway.Replied
                ? new NetworkFinding(FindingSeverity.Ok, "Devices", $"The gateway {gateway.Address} answers", gateway.Sentence, null)
                : new NetworkFinding(
                    FindingSeverity.Warn,
                    "Devices",
                    $"The gateway {gateway.Address} does not answer",
                    gateway.Sentence,
                    "Only matters for devices on other subnets. Check the gateway address is right; some routers ignore ping."));
        }

        if (inputs.PlanPings.Count == 0)
        {
            return;
        }

        List<(PlannedAddress Device, PingOutcome Outcome)> silent = [.. inputs.PlanPings.Where(p => !p.Outcome.Replied)];
        int answered = inputs.PlanPings.Count - silent.Count;

        if (silent.Count == 0)
        {
            findings.Add(new NetworkFinding(
                FindingSeverity.Ok,
                "Devices",
                F($"All {answered} planned device(s) on this subnet answer ping"),
                string.Empty,
                null));
            return;
        }

        // Silence is not evidence: a device may be off, unbuilt, or set to ignore ping. So this is a
        // note with the list, never an error - the same rule the plan comparison keeps.
        findings.Add(new NetworkFinding(
            answered == 0 ? FindingSeverity.Warn : FindingSeverity.Info,
            "Devices",
            F($"{silent.Count} of {inputs.PlanPings.Count} planned device(s) did not answer ping"),
            string.Join(", ", silent.Take(12).Select(s => $"{s.Device.Name} {s.Device.Address}")) + (silent.Count > 12 ? ", ..." : "."),
            answered == 0
                ? "None answered at all - check this PC is plugged into the right switch, and the adapter address and mask."
                : "They may be powered off, not yet built, or set to ignore ping. Start the ping monitor on one that should be there."));
    }

    private static void Traffic(TroubleshootInputs inputs, List<NetworkFinding> findings)
    {
        if (inputs.Traffic is { } traffic)
        {
            findings.AddRange(traffic.Findings.Where(f => f.Severity > FindingSeverity.Ok));
        }
    }

    private static string LinkSpeedText(long bps) => bps switch
    {
        <= 0 => "Link speed not reported.",
        >= 1_000_000_000 => F($"Link at {bps / 1_000_000_000.0:0.#} Gb/s."),
        _ => F($"Link at {bps / 1_000_000:0} Mb/s."),
    };

    private static string F(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
