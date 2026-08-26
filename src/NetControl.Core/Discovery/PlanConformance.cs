using System.Buffers.Binary;
using System.Net;
using NetControl.Core.Persistence;

namespace NetControl.Core.Discovery;

/// <summary>
/// Compares what is on the segment against what the plan says should be.
///
/// <para><b>This is what the active scan was for.</b> The plan knows what somebody intended and the
/// scan knows what is actually answering, and neither can produce the sentence that matters on its
/// own: <em>the address you planned for the conveyor drive is one the HMI is already sitting on</em>.
/// A duplicate check that only reads the plan cannot say it, because the HMI was never typed into
/// the plan - and the equipment that causes this is exactly the equipment nobody wrote down.</para>
///
/// <para><b>Silence is not evidence.</b> A planned device that did not answer produces no finding.
/// It may be powered down, behind a switch this scan did not reach, or simply not built yet, and
/// reporting absence as a problem would train people to ignore the list. The findings are only ever
/// about something that did answer.</para>
///
/// <para>Pure comparison: it sends nothing, reads nothing and changes nothing. The scan already
/// happened.</para>
/// </summary>
public static class PlanConformance
{
    /// <summary>
    /// Runs the comparison. Both arguments are read-only and neither is retained.
    /// </summary>
    /// <param name="plan">The plan rows, as the file holds them.</param>
    /// <param name="scan">What one scan found.</param>
    public static PlanConformanceReport Compare(IEnumerable<DeviceRecord> plan, DiscoveryResult scan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(scan);

        List<DeviceRecord> rows = [.. plan];
        IReadOnlyList<DiscoveredDevice> found = scan.Devices;

        // Answers grouped by the address the device claims, and deliberately not collapsed into one
        // entry each: two devices on one address is the finding, and a dictionary that kept the last
        // one to answer would erase it.
        Dictionary<IPAddress, List<DiscoveredDevice>> byAddress = [];
        foreach (DiscoveredDevice device in found)
        {
            if (!byAddress.TryGetValue(device.Address, out List<DiscoveredDevice>? answering))
            {
                answering = [];
                byAddress[device.Address] = answering;
            }

            answering.Add(device);
        }

        HashSet<MacAddress> plannedMacs = [.. rows.Select(row => row.Mac).Where(mac => !mac.IsEmpty)];
        HashSet<IPAddress> plannedAddresses = [.. rows.Where(row => row.PlannedIp is not null).Select(row => row.PlannedIp!)];

        List<PlanFinding> findings = [];
        int plannedWithAddress = 0;

        foreach (DeviceRecord row in rows)
        {
            // The scan result carrying this row's MAC, if one answered. A resolved MAC only: an
            // unresolved one is not evidence about which device this is, which is the whole reason
            // DiscoveredDevice keeps MacSource rather than a nullable MAC.
            DiscoveredDevice? itself = row.Mac.IsEmpty
                ? null
                : found.FirstOrDefault(device => device.CanBePlanned && device.Mac == row.Mac);

            if (row.PlannedIp is not { } planned)
            {
                if (itself is not null)
                {
                    findings.Add(new PlanFinding(
                        PlanFindingKind.PlannedDeviceUnaddressed,
                        EventSeverity.Info,
                        $"{Describe(row)} has no planned address, and answered the scan from {itself.Address}. "
                            + "That is what the device is holding now, not a suggestion - the address cell is "
                            + "still yours to fill in off the drawing.",
                        itself.Address,
                        row.Mac,
                        row.Id));
                }

                continue;
            }

            plannedWithAddress++;

            // Reported whatever is or is not sitting on the planned address, because they are two
            // different facts: "your device is not where you meant it to be" and "somebody else is
            // where you meant it to be" can both be true of the same row, and usually are.
            if (itself is not null && !itself.Address.Equals(planned))
            {
                findings.Add(new PlanFinding(
                    PlanFindingKind.PlannedDeviceElsewhere,
                    EventSeverity.Warn,
                    $"{Describe(row)} is planned for {planned} but answered from {itself.Address}. Either it has "
                        + "not been given its planned address yet, or something has moved it since.",
                    itself.Address,
                    row.Mac,
                    row.Id));
            }

            if (!byAddress.TryGetValue(planned, out List<DiscoveredDevice>? at) || at.Count == 0)
            {
                // Nothing answered there. Says nothing about whether the address is free - a scan
                // reaches what it reaches - so nothing is claimed.
                continue;
            }

            if (at.Count > 1)
            {
                findings.Add(new PlanFinding(
                    PlanFindingKind.ContestedAddress,
                    EventSeverity.Error,
                    $"{at.Count} devices answered on {planned}, which is planned for {Describe(row)}: "
                        + string.Join("; ", at.Select(Describe))
                        + ". Two devices on one address is the fault only a scan finds - nothing else in this "
                        + "tool would have noticed it.",
                    planned,
                    row.Mac,
                    row.Id));

                continue;
            }

            DiscoveredDevice occupant = at[0];

            if (!occupant.CanBePlanned)
            {
                findings.Add(new PlanFinding(
                    PlanFindingKind.AddressUnattributable,
                    EventSeverity.Warn,
                    $"Something is answering at {planned}, which is planned for {Describe(row)}, and it cannot "
                        + $"be told apart from that device. {occupant.PlanningObstacle}",
                    planned,
                    row.Mac,
                    row.Id));

                continue;
            }

            if (occupant.Mac == row.Mac)
            {
                findings.Add(new PlanFinding(
                    PlanFindingKind.Confirmed,
                    EventSeverity.Info,
                    $"{Describe(row)} answered at {planned}, which is where the plan puts it.",
                    planned,
                    row.Mac,
                    row.Id));

                continue;
            }

            findings.Add(new PlanFinding(
                PlanFindingKind.AddressTakenByAnother,
                EventSeverity.Error,
                $"{planned} is planned for {Describe(row)}, but {Describe(occupant)} is already answering there. "
                    + "Nothing has been changed. Serving the plan will not move it - BOOTP answers the MAC it was "
                    + "asked about - so both would then be claiming the address.",
                planned,
                row.Mac,
                row.Id));
        }

        foreach (DiscoveredDevice device in found)
        {
            // Already spoken for: it is a planned device, confirmed or somewhere else.
            if (device.CanBePlanned && plannedMacs.Contains(device.Mac))
            {
                continue;
            }

            // Already spoken for: it is the thing sitting on somebody's planned address.
            if (plannedAddresses.Contains(device.Address))
            {
                continue;
            }

            findings.Add(new PlanFinding(
                PlanFindingKind.NotInPlan,
                EventSeverity.Info,
                $"{Describe(device)} answered at {device.Address} and is not in the plan."
                    + (device.CanBePlanned ? string.Empty : " " + device.PlanningObstacle),
                device.Address,
                device.Mac));
        }

        return new PlanConformanceReport(
            [.. findings
                .OrderByDescending(finding => (int)finding.Severity)
                .ThenBy(finding => Key(finding.Address))
                .ThenBy(finding => (int)finding.Kind)],
            plannedWithAddress,
            found.Count);
    }

    /// <summary>
    /// A plan row as somebody would say it out loud. <c>DisplayName</c> is the one naming rule and
    /// it lives on the record, so this cannot disagree with the grid or the request log.
    /// </summary>
    private static string Describe(DeviceRecord row) =>
        row.DisplayName is { } name ? $"{name} ({row.Mac})" : row.Mac.ToString();

    /// <summary>
    /// A scanned device the same way. The product name first, because on a panel that is what
    /// somebody is looking for - and it is doing the work an ODVA vendor table would otherwise do.
    /// </summary>
    private static string Describe(DiscoveredDevice device)
    {
        string name = device.ProductName.Length == 0 ? "an unnamed device" : device.ProductName;
        return device.Mac.IsEmpty ? name : $"{name} ({device.Mac})";
    }

    /// <summary>
    /// Sorts addresses as numbers rather than as text, so .2 comes before .100. Anything that is
    /// not four bytes sorts first; this tool is IPv4 only and one turning up here would be a bug
    /// worth seeing at the top of the list rather than buried in it.
    /// </summary>
    private static uint Key(IPAddress? address)
    {
        if (address is null)
        {
            return 0;
        }

        byte[] octets = address.GetAddressBytes();
        return octets.Length == 4 ? BinaryPrimitives.ReadUInt32BigEndian(octets) : 0;
    }
}
