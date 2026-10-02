using System.Buffers.Binary;
using System.Net;
using NetControl.Core.Persistence;

namespace NetControl.Core.Discovery;

/// <summary>
/// What changed between two scans of the same subnet. Pure: two lists in, a list of changes out.
///
/// <para><b>Identity, not address, decides what is "the same device".</b> Vendor, product code and
/// serial - <see cref="ScanSightingRecord.IdentityKey"/> - so a drive that was re-addressed is one
/// device that moved rather than one that vanished and one that appeared. That is the change worth
/// the most on a call-out: somebody moved something, and everything pointing at its old address
/// has stopped working.</para>
///
/// <para><b>Silence is not absence</b>, the rule <see cref="PlanConformance"/> already keeps. A device
/// that answered last time and not this time may be powered down for the shift, so it is Info and
/// worded as "not answering", never as "removed".</para>
///
/// <para>Each device is spoken about once. The device that used to be at an address now held by a
/// replacement is reported as part of the replacement, not again as "not answering".</para>
/// </summary>
public static class InventoryDiff
{
    public static InventoryDiffResult Compare(
        IReadOnlyList<ScanSightingRecord> previous,
        IReadOnlyList<ScanSightingRecord> current,
        DateTimeOffset previousUtc)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);

        Dictionary<string, ScanSightingRecord> before = ByKey(previous);
        Dictionary<string, ScanSightingRecord> now = ByKey(current);

        var accounted = new HashSet<string>();
        var changes = new List<InventoryChange>();
        int unchanged = 0;

        foreach ((string key, ScanSightingRecord device) in now)
        {
            if (before.TryGetValue(key, out ScanSightingRecord? was))
            {
                accounted.Add(key);
                bool same = true;

                if (!was.Address.Equals(device.Address))
                {
                    same = false;
                    changes.Add(new InventoryChange(
                        InventoryChangeKind.Moved,
                        EventSeverity.Warn,
                        device.Address,
                        $"{device.Name} moved from {was.Address} to {device.Address}. Anything still pointing at "
                        + $"{was.Address} - a controller's I/O tree, an HMI, a message instruction - has lost it."));
                }

                if (!string.Equals(was.Revision, device.Revision, StringComparison.Ordinal))
                {
                    same = false;
                    changes.Add(new InventoryChange(
                        InventoryChangeKind.FirmwareChanged,
                        EventSeverity.Info,
                        device.Address,
                        $"{device.Name} at {device.Address} now reports firmware {device.Revision} (was "
                        + $"{was.Revision}). Worth knowing on a line validated against a revision."));
                }

                if (same)
                {
                    unchanged++;
                }

                continue;
            }

            // Not seen before by identity. Was something else at this address last time - something
            // that is not still answering somewhere else under its own identity?
            ScanSightingRecord? displaced = previous.FirstOrDefault(old =>
                old.Address.Equals(device.Address)
                && !now.ContainsKey(old.IdentityKey)
                && !accounted.Contains(old.IdentityKey));

            if (displaced is not null)
            {
                accounted.Add(displaced.IdentityKey);

                bool sameProduct = displaced.VendorId == device.VendorId && displaced.ProductCode == device.ProductCode;

                changes.Add(sameProduct
                    ? new InventoryChange(
                        InventoryChangeKind.Replaced,
                        EventSeverity.Info,
                        device.Address,
                        $"The {device.ProductName} at {device.Address} has been replaced: serial "
                        + $"{displaced.SerialText} last time, {device.SerialText} now. Check the replacement "
                        + "is configured like the module it replaced.")
                    : new InventoryChange(
                        InventoryChangeKind.DifferentDevice,
                        EventSeverity.Warn,
                        device.Address,
                        $"{device.Address} is now answered by {device.Name}; last time it was {displaced.Name}. "
                        + "Either the old device was moved or removed, or two devices now want this address."));

                continue;
            }

            changes.Add(new InventoryChange(
                InventoryChangeKind.New,
                EventSeverity.Info,
                device.Address,
                $"{device.Name} at {device.Address} was not in the scan of this subnet last time."));
        }

        foreach ((string key, ScanSightingRecord was) in before)
        {
            if (accounted.Contains(key) || now.ContainsKey(key))
            {
                continue;
            }

            changes.Add(new InventoryChange(
                InventoryChangeKind.NotAnswering,
                EventSeverity.Info,
                was.Address,
                $"{was.Name} answered at {was.Address} last time and did not answer this time. It may be powered "
                + "down, or behind a switch this scan did not reach."));
        }

        // Worst first, then by address as a number, so .2 comes before .100 and the list reads in the
        // order the panel does - the same rule PlanConformance keeps.
        List<InventoryChange> ordered = [.. changes
            .OrderByDescending(change => change.Severity)
            .ThenBy(change => Numeric(change.Address))
            .ThenBy(change => change.Kind)];

        return new InventoryDiffResult(ordered, unchanged, previousUtc);
    }

    /// <summary>First sighting of each identity wins. A scan has already deduped by identity, so a
    /// repeat here can only come from a file somebody edited.</summary>
    private static Dictionary<string, ScanSightingRecord> ByKey(IReadOnlyList<ScanSightingRecord> sightings)
    {
        var map = new Dictionary<string, ScanSightingRecord>(StringComparer.Ordinal);

        foreach (ScanSightingRecord sighting in sightings)
        {
            map.TryAdd(sighting.IdentityKey, sighting);
        }

        return map;
    }

    private static uint Numeric(IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[4];
        return address.TryWriteBytes(bytes, out int written) && written == 4
            ? BinaryPrimitives.ReadUInt32BigEndian(bytes)
            : uint.MaxValue;
    }
}
