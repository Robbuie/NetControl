namespace NetControl.Core.Persistence;

/// <summary>
/// One row of the append-only event log.
///
/// This is the commissioning record: the thing you hand a customer, and the thing you read six
/// months later when somebody asks why a drive has the address it has. It is a structured record
/// rather than a formatted string so that the UI, the HTML report and a future CSV export can
/// each present it their own way without parsing English back out of a log line.
/// </summary>
public sealed record EventRecord
{
    /// <summary>Row id. 0 until <see cref="EventLog.Append(EventRecord)"/> has written it.</summary>
    public long Id { get; init; }

    public required DateTimeOffset Utc { get; init; }

    public EventSeverity Severity { get; init; } = EventSeverity.Info;

    public required EventCategory Category { get; init; }

    /// <summary>The planned device this concerns, when there is one. Null for an unknown MAC.</summary>
    public long? DeviceId { get; init; }

    /// <summary>
    /// The MAC or IP the event is about, as text. Present even when <see cref="DeviceId"/> is
    /// null - "00:1D:9C:C7:B0:70 asked and is not in the plan" is one of the more useful lines
    /// this log can carry, and it has no device row to point at.
    /// </summary>
    public string? Target { get; init; }

    /// <summary>One sentence, already user-facing. Names the cause, not just the symptom.</summary>
    public required string Message { get; init; }

    /// <summary>
    /// JSON: transaction ids, raw option bytes, status codes - whatever the reader needs to work
    /// out what really happened. Null when there is nothing worth keeping.
    /// </summary>
    public string? Detail { get; init; }
}
