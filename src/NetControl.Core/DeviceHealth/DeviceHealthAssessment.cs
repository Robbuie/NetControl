using System.Globalization;
using NetControl.Core.Cip;
using NetControl.Core.Persistence;

namespace NetControl.Core.DeviceHealth;

/// <summary>
/// What a diagnostics read means: a pure function from readings to findings, so every rule here is
/// tested without a socket or a device.
///
/// <para><b>Counters are graded by whether they are still moving.</b> A single read says what has
/// happened since power-up or since somebody cleared the counters, and a handful of FCS errors from
/// the day the cable was plugged in is not a fault. So a first read reports error counters as Info
/// and says to read again; a second read of the same device reports what moved in between, and
/// <em>that</em> is a Warn. The exceptions are the counters that should never be anything but zero
/// on a healthy switched network - late collisions, and any collision on a full-duplex port - which
/// are a Warn on sight.</para>
///
/// <para>Every message names a likely cause and a next action, per the conventions in CLAUDE.md.
/// "FCS errors: 14" is a number; "14 frames arrived damaged - cable, connector, noise or a duplex
/// mismatch" is the product.</para>
/// </summary>
public static class DeviceHealthAssessment
{
    public static DeviceHealthResult Assess(DeviceHealthReport current, DeviceHealthReport? previous = null)
    {
        ArgumentNullException.ThrowIfNull(current);

        var findings = new List<HealthFinding>();

        AssessIdentity(current, findings);
        AssessTcpIp(current, findings);

        bool comparable = IsComparable(current, previous, findings, out TimeSpan elapsed);

        foreach (LinkPort port in current.Ports)
        {
            LinkPort? before = comparable
                ? previous!.Ports.FirstOrDefault(p => p.Instance == port.Instance)
                : null;

            AssessPort(port, current.Ports.Count, findings);
            AssessCounters(port, before, elapsed, findings);
        }

        // Worst first, then by port, so the list reads top-down in the order somebody should act.
        List<HealthFinding> ordered = [.. findings
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => f.Port ?? 0)];

        return new DeviceHealthResult(current, ordered);
    }

    private static void AssessIdentity(DeviceHealthReport report, List<HealthFinding> findings)
    {
        if (report.Status is not { } status)
        {
            return;
        }

        if (status.MajorUnrecoverableFault)
        {
            findings.Add(new HealthFinding(
                EventSeverity.Error,
                $"The device reports a major unrecoverable fault ({status.ExtendedStatusText}). It needs attention "
                + "at the cabinet - its status LEDs and its own web page will say more, and it may need replacing."));
        }
        else if (status.MajorRecoverableFault)
        {
            findings.Add(new HealthFinding(
                EventSeverity.Error,
                $"The device reports a major recoverable fault ({status.ExtendedStatusText}). Find and fix the cause, "
                + "then power-cycle or reset it to clear the fault."));
        }

        if (status.MinorUnrecoverableFault)
        {
            findings.Add(new HealthFinding(
                EventSeverity.Warn,
                "The device reports a minor unrecoverable fault. It is still running; check its diagnostics page "
                + "before relying on it."));
        }
        else if (status.MinorRecoverableFault)
        {
            findings.Add(new HealthFinding(
                EventSeverity.Warn,
                "The device reports a minor recoverable fault - often a lost or timed-out connection. Its "
                + "diagnostics page will name it."));
        }

        switch (status.ExtendedStatus)
        {
            case 2 when !status.HasMajorFault:
                findings.Add(new HealthFinding(
                    EventSeverity.Warn,
                    "At least one I/O connection to the device has faulted. The controller that owns it will be "
                    + "showing the same fault - check the RPI, the address in the I/O tree, and the switch."));
                break;

            case 4:
                findings.Add(new HealthFinding(
                    EventSeverity.Warn,
                    "The device says its non-volatile configuration is bad. It may come back from a power cycle "
                    + "with defaults - including its address."));
                break;

            case 1:
                findings.Add(new HealthFinding(
                    EventSeverity.Info,
                    "A firmware update is in progress on the device. Leave it powered."));
                break;
        }
    }

    private static void AssessTcpIp(DeviceHealthReport report, List<HealthFinding> findings)
    {
        if (report.TcpIpStatus is { } status)
        {
            if (status.ConflictFault)
            {
                findings.Add(new HealthFinding(
                    EventSeverity.Error,
                    $"Address conflict fault: the device found another station on {report.Address} and has given "
                    + "up the address. Find the other device - a scan, or the ARP table - before reconnecting."));
            }
            else if (status.ConflictDetected)
            {
                findings.Add(new HealthFinding(
                    EventSeverity.Error,
                    $"Address conflict detected: another station has been seen on {report.Address}. Two devices on "
                    + "one address will each work some of the time. Find the other one before anything else."));
            }

            if (status.ConfigurationPending)
            {
                findings.Add(new HealthFinding(
                    EventSeverity.Warn,
                    "A configuration has been written to the device and is waiting for a reset or power cycle. "
                    + "Until then it is still running on the old one - which is why a readback can disagree."));
            }

            if (status.ConfigurationStatus == 2)
            {
                findings.Add(new HealthFinding(
                    EventSeverity.Info,
                    "The address comes from hardware switches on the module. Set static cannot change it; the "
                    + "switches can."));
            }
        }

        if (report.Method is ConfigMethod.Bootp or ConfigMethod.Dhcp)
        {
            findings.Add(new HealthFinding(
                EventSeverity.Info,
                $"The device is still a {(report.Method == ConfigMethod.Bootp ? "BOOTP" : "DHCP")} client. It will "
                + "ask for an address again at its next power cycle; Set static makes the address it has permanent."));
        }

        if (report.Configuration is { } configuration && !configuration.Ip.Equals(report.Address))
        {
            findings.Add(new HealthFinding(
                EventSeverity.Warn,
                $"The device answered at {report.Address} but says its address is {configuration.Ip}. Usually NAT, a "
                + "second interface, or a configuration that has been written and not yet applied."));
        }
    }

    private static void AssessPort(LinkPort port, int portCount, List<HealthFinding> findings)
    {
        string where = portCount > 1
            ? string.Create(CultureInfo.InvariantCulture, $"Port {port.Instance}")
            : "The port";

        if (port.Flags is not { } flags)
        {
            return;
        }

        if (flags.LocalHardwareFault)
        {
            findings.Add(new HealthFinding(
                EventSeverity.Error,
                $"{where} reports a fault in its own hardware. Move the cable to another port if there is one; "
                + "otherwise the module needs replacing.",
                port.Instance));
        }

        if (!flags.LinkActive)
        {
            // Only worth saying on a multi-port device - the only way a single-port device can be
            // read at all is over that port. An unused second port is normal, so Info.
            if (portCount > 1)
            {
                findings.Add(new HealthFinding(EventSeverity.Info, $"{where} has no link.", port.Instance));
            }

            return;
        }

        if (!flags.FullDuplex)
        {
            findings.Add(new HealthFinding(
                EventSeverity.Warn,
                $"{where} is running half duplex. On a switched network that is almost always a duplex mismatch - "
                + "one end forced, the other left on auto. Set both ends to auto, or force both the same.",
                port.Instance));
        }

        switch (flags.Negotiation)
        {
            case NegotiationStatus.FailedUsingDefaults:
                findings.Add(new HealthFinding(
                    EventSeverity.Warn,
                    $"{where} could not auto-negotiate speed or duplex and is running on its defaults. Check the "
                    + "switch port's speed setting and the cable.",
                    port.Instance));
                break;

            case NegotiationStatus.DuplexDefaulted:
                findings.Add(new HealthFinding(
                    EventSeverity.Warn,
                    $"{where} detected the speed but could not negotiate duplex, so it fell back to half. The far "
                    + "end is probably forced - set it to auto, or force this end to match.",
                    port.Instance));
                break;

            case NegotiationStatus.Forced:
                findings.Add(new HealthFinding(
                    EventSeverity.Info,
                    $"{where} has its speed and duplex forced. The switch port must be forced to exactly the same, "
                    + "or the link will run with a duplex mismatch.",
                    port.Instance));
                break;

            case NegotiationStatus.InProgress:
                findings.Add(new HealthFinding(
                    EventSeverity.Info,
                    $"{where} is still auto-negotiating. Read again in a few seconds.",
                    port.Instance));
                break;
        }

        if (port.SpeedMbps == 10)
        {
            findings.Add(new HealthFinding(
                EventSeverity.Warn,
                $"{where} linked at 10 Mb/s. Usually a damaged pair in the cable or a far end that cannot do "
                + "better; most devices here should manage 100.",
                port.Instance));
        }

        if (flags.ManualSettingRequiresReset)
        {
            findings.Add(new HealthFinding(
                EventSeverity.Info,
                $"{where} has a speed or duplex change waiting for a reset.",
                port.Instance));
        }
    }

    private static void AssessCounters(
        LinkPort port, LinkPort? before, TimeSpan elapsed, List<HealthFinding> findings)
    {
        string where = string.Create(CultureInfo.InvariantCulture, $"port {port.Instance}");
        bool fullDuplex = port.Flags is { LinkActive: true, FullDuplex: true };

        if (port.Media is { } media)
        {
            MediaCounters? was = before?.Media;

            if (was is not null && WentBackwards(media, was))
            {
                findings.Add(new HealthFinding(
                    EventSeverity.Info,
                    $"The media counters on {where} went backwards since the last read - the device restarted, or "
                    + "something cleared them. Nothing is compared this time.",
                    port.Instance));
                was = null;
            }

            // Never anything but zero on a healthy switched link, so a Warn on sight.
            Counter(findings, port.Instance, where, media.LateCollisions, was?.LateCollisions, elapsed,
                "late collision(s)",
                "the duplex mismatch signature - one end half duplex, the other full. Set both to auto.",
                warnOnTotal: true);

            if (fullDuplex)
            {
                ulong collisions = media.Collisions - media.LateCollisions;
                ulong? wasCollisions = was is null ? null : was.Collisions - was.LateCollisions;

                Counter(findings, port.Instance, where, collisions, wasCollisions, elapsed,
                    "collision(s) on a full-duplex link",
                    "which should be impossible - the far end is almost certainly half duplex.",
                    warnOnTotal: true);
            }

            Counter(findings, port.Instance, where, (ulong)media.FcsErrors + media.AlignmentErrors,
                was is null ? null : (ulong)was.FcsErrors + was.AlignmentErrors, elapsed,
                "frame(s) arrived damaged (FCS or alignment errors)",
                "a cable, a connector, electrical noise near the run, or a duplex mismatch.");

            Counter(findings, port.Instance, where, media.FrameTooLong, was?.FrameTooLong, elapsed,
                "frame(s) too long",
                "usually VLAN tags or jumbo frames reaching a device that cannot take them - check the switch "
                + "port is an untagged access port.");

            Counter(findings, port.Instance, where, (ulong)media.MacReceiveErrors + media.MacTransmitErrors
                + media.CarrierSenseErrors,
                was is null ? null : (ulong)was.MacReceiveErrors + was.MacTransmitErrors + was.CarrierSenseErrors,
                elapsed,
                "MAC or carrier-sense error(s)",
                "the physical layer - cable, connector or the port itself.");
        }

        if (port.Interface is { } counters)
        {
            InterfaceCounters? was = before?.Interface;

            if (was is not null && WentBackwards(counters, was))
            {
                was = null;
            }

            Counter(findings, port.Instance, where, counters.InDiscards, was?.InDiscards, elapsed,
                "received frame(s) discarded",
                "the device is receiving more than it can handle - often multicast flooding on a switch without "
                + "IGMP snooping, or a broadcast storm.");

            Counter(findings, port.Instance, where, (ulong)counters.InErrors + counters.OutErrors,
                was is null ? null : (ulong)was.InErrors + was.OutErrors, elapsed,
                "frame(s) in error",
                "the cable, the connector, or the switch port.");

            if (was is not null && elapsed > TimeSpan.Zero && counters.InNonUnicastPackets >= was.InNonUnicastPackets)
            {
                double perSecond = (counters.InNonUnicastPackets - was.InNonUnicastPackets) / elapsed.TotalSeconds;

                if (perSecond >= 1)
                {
                    findings.Add(new HealthFinding(
                        EventSeverity.Info,
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"{Capitalise(where)} is receiving about {perSecond:0} broadcast or multicast frame(s) a "
                            + $"second. Thousands a second on a device that consumes no multicast I/O points at a "
                            + $"switch without IGMP snooping."),
                        port.Instance));
                }
            }
        }
    }

    /// <summary>
    /// One counter, graded. With no earlier read: Info, saying it is a total and to read again -
    /// unless <paramref name="warnOnTotal"/>. With an earlier read of the same device: a Warn if it
    /// moved, and nothing at all if it did not.
    /// </summary>
    private static void Counter(
        List<HealthFinding> findings,
        ushort instance,
        string where,
        ulong now,
        ulong? before,
        TimeSpan elapsed,
        string what,
        string cause,
        bool warnOnTotal = false)
    {
        if (before is { } was)
        {
            if (now <= was)
            {
                return;
            }

            ulong moved = now - was;
            findings.Add(new HealthFinding(
                EventSeverity.Warn,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{moved} {what} on {where} in the last {Seconds(elapsed)} - still counting. Likely {cause}"),
                instance));
            return;
        }

        if (now == 0)
        {
            return;
        }

        findings.Add(new HealthFinding(
            warnOnTotal ? EventSeverity.Warn : EventSeverity.Info,
            warnOnTotal
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $"{now} {what} on {where} since its counters were last cleared - {cause}")
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"{now} {what} on {where} since its counters were last cleared. A few from plugging cables in "
                    + $"is normal; read again in a minute to see whether it is still counting. If it is: {cause}"),
            instance));
    }

    /// <summary>
    /// Whether two reads can be compared: an earlier read exists, of the same address, of the same
    /// device, and some time passed. A different serial at the same address is said out loud - a
    /// module was swapped between the two reads, and comparing its counters to its predecessor's
    /// would invent numbers.
    /// </summary>
    private static bool IsComparable(
        DeviceHealthReport current, DeviceHealthReport? previous, List<HealthFinding> findings, out TimeSpan elapsed)
    {
        elapsed = TimeSpan.Zero;

        if (previous is null || !previous.Address.Equals(current.Address))
        {
            return false;
        }

        if (current.SerialNumber is not null && previous.SerialNumber is not null && !current.IsSameDeviceAs(previous))
        {
            findings.Add(new HealthFinding(
                EventSeverity.Info,
                $"This is not the device that answered at {current.Address} last time (serial {previous.SerialText}, "
                + $"now {current.SerialText}). Its counters are not compared with the other one's."));
            return false;
        }

        elapsed = current.ReadUtc - previous.ReadUtc;
        return elapsed > TimeSpan.Zero;
    }

    private static bool WentBackwards(MediaCounters now, MediaCounters was) =>
        now.FcsErrors < was.FcsErrors
        || now.AlignmentErrors < was.AlignmentErrors
        || now.LateCollisions < was.LateCollisions
        || now.SingleCollisions < was.SingleCollisions
        || now.MultipleCollisions < was.MultipleCollisions
        || now.FrameTooLong < was.FrameTooLong
        || now.MacReceiveErrors < was.MacReceiveErrors;

    private static bool WentBackwards(InterfaceCounters now, InterfaceCounters was) =>
        now.InUnicastPackets < was.InUnicastPackets
        || now.InDiscards < was.InDiscards
        || now.InErrors < was.InErrors;

    private static string Seconds(TimeSpan elapsed) =>
        elapsed.TotalSeconds < 120
            ? string.Create(CultureInfo.InvariantCulture, $"{elapsed.TotalSeconds:0} s")
            : string.Create(CultureInfo.InvariantCulture, $"{elapsed.TotalMinutes:0} min");

    private static string Capitalise(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
