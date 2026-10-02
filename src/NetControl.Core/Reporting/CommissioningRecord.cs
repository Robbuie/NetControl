using System.Text.Json;
using NetControl.Core.Persistence;

namespace NetControl.Core.Reporting;

/// <summary>
/// Reads each device's last finished commissioning operation back out of the event log.
///
/// <para><b>Only the record decides.</b> A device is Verified here only when the last operation that
/// finished against it was a Set static whose readback was recorded as Verified - the same rule as
/// the grid, which is that only a readback sets Verified. The record of that readback is the
/// evidence, which is why the report and a reopened project can both trust it.</para>
///
/// <para>The rows it reads are the ones the grid writes when an operation finishes: category Cip,
/// attributed to a device, with an <c>outcome</c> in the detail - and an <c>operation</c> of
/// <c>enableBootp</c> or <c>enableDhcp</c> when it was a hand-back. Progress steps carry no outcome
/// and are skipped. A detail column that does not parse - a file somebody edited - is skipped too,
/// rather than taken as evidence of anything.</para>
/// </summary>
public static class CommissioningRecord
{
    /// <summary>The last finished operation for every device that has one, keyed by device id.</summary>
    public static IReadOnlyDictionary<long, DeviceCommissioning> LastOutcomes(IEnumerable<EventRecord> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        var last = new Dictionary<long, DeviceCommissioning>();

        // In id order, so a later row replaces an earlier one; ids are the order rows were appended.
        foreach (EventRecord row in events.OrderBy(e => e.Id))
        {
            if (row.Category != EventCategory.Cip || row.DeviceId is not { } deviceId || row.Detail is null
                || !TryRead(row.Detail, out string? outcome, out string? operation, out string? readback))
            {
                continue;
            }

            bool verified = string.Equals(outcome, "Verified", StringComparison.Ordinal);
            bool handBack = operation is not null && operation.StartsWith("enable", StringComparison.Ordinal);

            CommissioningOutcome state = (verified, handBack) switch
            {
                (true, false) => CommissioningOutcome.Verified,
                (true, true) => CommissioningOutcome.HandedBack,
                _ => CommissioningOutcome.NotVerified,
            };

            last[deviceId] = new DeviceCommissioning(deviceId, state, row.Utc, row.Message, readback);
        }

        return last;
    }

    private static bool TryRead(string detail, out string? outcome, out string? operation, out string? readback)
    {
        outcome = null;
        operation = null;
        readback = null;

        try
        {
            using JsonDocument document = JsonDocument.Parse(detail);

            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("outcome", out JsonElement outcomeElement)
                || outcomeElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            outcome = outcomeElement.GetString();

            if (document.RootElement.TryGetProperty("operation", out JsonElement operationElement)
                && operationElement.ValueKind == JsonValueKind.String)
            {
                operation = operationElement.GetString();
            }

            if (document.RootElement.TryGetProperty("readback", out JsonElement readbackElement)
                && readbackElement.ValueKind == JsonValueKind.String)
            {
                readback = readbackElement.GetString();
            }

            return outcome is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
