using NetControl.Core.Persistence;

namespace NetControl.Core.Discovery;

/// <summary>
/// What one scan had to say about the plan.
///
/// <para>The scan knows what is on the segment and the plan knows what is meant to be; this is the
/// sentence neither of them can produce alone - "the address you planned for the conveyor drive is
/// one the HMI is already sitting on".</para>
/// </summary>
/// <param name="Findings">
/// Worst first, so the conflicts are read before the confirmations. Empty when the scan found
/// nothing that bears on the plan, which is not the same as everything being fine.
/// </param>
/// <param name="PlannedWithAddress">
/// How many plan rows carried an address to compare against. A plan of MACs with no addresses yet
/// is the normal shape at 7am, and cannot be confirmed or contradicted by anything.
/// </param>
/// <param name="DevicesFound">How many devices answered the scan.</param>
public sealed record PlanConformanceReport(
    IReadOnlyList<PlanFinding> Findings,
    int PlannedWithAddress,
    int DevicesFound)
{
    /// <summary>No scan has been compared against a plan yet, or there was nothing to compare.</summary>
    public static PlanConformanceReport None { get; } = new([], 0, 0);

    /// <summary>The findings that should stop somebody before they serve the plan.</summary>
    public IReadOnlyList<PlanFinding> Conflicts => [.. Findings.Where(finding => finding.IsConflict)];

    public bool HasConflicts => Findings.Any(finding => finding.IsConflict);

    /// <summary>Planned devices that answered where the plan puts them.</summary>
    public int ConfirmedCount => Count(PlanFindingKind.Confirmed);

    /// <summary>
    /// How the comparison should be graded in the event log. A scan that found a contested or an
    /// occupied planned address is not an informational scan.
    /// </summary>
    public EventSeverity Severity =>
        HasConflicts ? EventSeverity.Error
        : Findings.Any(finding => finding.Severity == EventSeverity.Warn) ? EventSeverity.Warn
        : EventSeverity.Info;

    /// <summary>
    /// One line for the status area and the event log, so the two cannot disagree.
    ///
    /// <para>It leads with the count that was <em>compared</em> rather than the count that was
    /// found, deliberately. "8 devices answered" is reassuring right up until somebody notices the
    /// plan has fourteen rows in it.</para>
    /// </summary>
    public string Summary
    {
        get
        {
            if (PlannedWithAddress == 0)
            {
                return DevicesFound == 0
                    ? "Nothing answered, and no plan row has an address to compare against."
                    : $"{Devices(DevicesFound)} answered, and no plan row has an address to compare against.";
            }

            return $"{ConfirmedCount} of the {PlannedWithAddress} planned addresses answered as planned."
                + Tally(
                    PlanFindingKind.AddressTakenByAnother,
                    "1 planned address is held by a different device.",
                    "planned addresses are held by different devices.")
                + Tally(
                    PlanFindingKind.ContestedAddress,
                    "1 planned address had more than one device answer on it.",
                    "planned addresses had more than one device answer on them.")
                + Tally(
                    PlanFindingKind.AddressUnattributable,
                    "1 planned address was answered by something that could not be identified.",
                    "planned addresses were answered by something that could not be identified.")
                + Tally(
                    PlanFindingKind.PlannedDeviceElsewhere,
                    "1 planned device answered from somewhere else.",
                    "planned devices answered from somewhere else.")
                + Tally(
                    PlanFindingKind.NotInPlan,
                    "1 device answered that is not in the plan.",
                    "devices answered that are not in the plan.");
        }
    }

    public int Count(PlanFindingKind kind) => Findings.Count(finding => finding.Kind == kind);

    private static string Devices(int count) => count == 1 ? "1 device" : $"{count} devices";

    private string Tally(PlanFindingKind kind, string one, string manySuffix) => Count(kind) switch
    {
        0 => string.Empty,
        1 => " " + one,
        int many => $" {many} {manySuffix}",
    };
}
