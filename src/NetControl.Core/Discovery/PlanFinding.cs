using System.Net;
using NetControl.Core.Persistence;

namespace NetControl.Core.Discovery;

/// <summary>
/// One thing a scan can say about the plan, already worded.
///
/// <para>The wording lives in Core for the same reason <c>DeviceRecord.TryToAssignment</c>'s and
/// <c>DiscoveredDevice.PlanningObstacle</c>'s do: the scan list, the row it belongs to and the
/// event log must not be able to describe the same finding three different ways.</para>
/// </summary>
/// <param name="Kind">Which finding this is.</param>
/// <param name="Severity">
/// How much attention it needs, on the same scale the event log uses - so a finding can be written
/// into the record without being re-graded on the way in.
/// </param>
/// <param name="Message">
/// What was found and what it means, naming the next action where there is one. Already
/// user-facing; nothing rephrases it.
/// </param>
/// <param name="Address">The address the finding is about, when it is about one.</param>
/// <param name="Mac">The planned device's hardware address, when the finding is about a plan row.</param>
/// <param name="DeviceId">
/// The plan row this is about, so a UI can select it and the event log can attribute it. 0 when the
/// finding is about a device that is not in the plan.
/// </param>
public sealed record PlanFinding(
    PlanFindingKind Kind,
    EventSeverity Severity,
    string Message,
    IPAddress? Address = null,
    MacAddress Mac = default,
    long DeviceId = 0)
{
    /// <summary>True for the findings that should stop somebody before they serve the plan.</summary>
    public bool IsConflict => Severity == EventSeverity.Error;

    public override string ToString() => Message;
}
