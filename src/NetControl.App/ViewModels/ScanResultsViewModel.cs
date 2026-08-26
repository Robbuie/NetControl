using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using NetControl.Core;
using NetControl.Core.Discovery;
using NetControl.Core.Persistence;

namespace NetControl.App.ViewModels;

/// <summary>
/// What the last scan found, as a list.
///
/// <para><b>It is deliberately not the request log.</b> "This device asked for an address" and
/// "this device answered when I asked who was there" are different facts about different devices -
/// a device that answers a scan already has an address and does not need BOOTP - and a list that
/// mixed them would be a list that misleads. They share one thing: the same "add to plan" action,
/// which is why both gestures end up in <see cref="MainViewModel"/>.</para>
///
/// <para>This replaces its contents on each scan rather than accumulating. A scan is a snapshot of
/// a segment at a moment; keeping the previous one alongside would mean a device that has since
/// been unplugged sitting in the list looking present. The event log is where the history goes.</para>
/// </summary>
public sealed partial class ScanResultsViewModel : ObservableObject
{
    [ObservableProperty]
    private ScanResultViewModel? _selectedRow;

    /// <summary>True while a scan is in flight, so the button can say so and not be pressed twice.</summary>
    [ObservableProperty]
    private bool _isScanning;

    /// <summary>
    /// One line about the last scan: what went out, what came back, and anything odd about it.
    /// Starts as an explanation of what the button does, because an empty list with no caption
    /// looks like a feature that failed rather than one nobody has used yet.
    /// </summary>
    [ObservableProperty]
    private string _status =
        "No scan yet. Scan asks every EtherNet/IP device on the selected adapter to identify itself.";

    /// <summary>
    /// What the last scan had to say about the plan. <see cref="PlanConformanceReport.None"/> until
    /// a scan has been compared against one.
    /// </summary>
    [ObservableProperty]
    private PlanConformanceReport _conformance = PlanConformanceReport.None;

    public ObservableCollection<ScanResultViewModel> Rows { get; } = [];

    /// <summary>
    /// The findings worth putting on screen as a list: the ones that need a person to look.
    ///
    /// <para>Confirmations and devices that are simply not in the plan are deliberately left out.
    /// They are already said twice - in the row's own note and in the summary's tally - and a list
    /// that scrolls is a list nobody reads to the bottom of, which is where the conflicts would
    /// end up on a segment with plenty of switches on it.</para>
    /// </summary>
    public ObservableCollection<PlanFinding> Findings { get; } = [];

    public bool HasResults => Rows.Count > 0;

    public bool HasFindings => Findings.Count > 0;

    /// <summary>Replaces the list with what a scan found.</summary>
    public void Apply(DiscoveryResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        Rows.Clear();

        foreach (DiscoveredDevice device in result.Devices)
        {
            Rows.Add(new ScanResultViewModel(device));
        }

        SelectedRow = null;
        Status = result.Summary;

        // The previous scan's comparison does not survive a new scan, even for a moment: it was
        // about devices that are no longer in this list.
        ApplyConformance(PlanConformanceReport.None);

        OnPropertyChanged(nameof(HasResults));
    }

    /// <summary>
    /// Applies what the plan says about what was found - see <see cref="PlanConformance"/>. The
    /// findings go into the list, and each one that is about a device on screen goes into that
    /// device's own row as well, because a finding read next to the thing it is about needs no
    /// matching up by eye.
    /// </summary>
    public void ApplyConformance(PlanConformanceReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        Conformance = report;

        Findings.Clear();
        foreach (PlanFinding finding in report.Findings.Where(f => f.Severity != EventSeverity.Info))
        {
            Findings.Add(finding);
        }

        foreach (ScanResultViewModel row in Rows)
        {
            // A device that is simply not in the plan is not told so in its own row: the row
            // already says whether it is planned, and repeating it as a finding is noise on the
            // majority of rows on any real segment.
            PlanFinding[] about =
                [.. report.Findings
                    .Where(f => f.Kind != PlanFindingKind.NotInPlan
                        && f.Address is not null
                        && f.Address.Equals(row.Device.Address))
                    .OrderByDescending(f => (int)f.Severity)];

            row.PlanNote = about.Length == 0 ? null : about[0].Message;
            row.PlanConflict = about.Any(f => f.IsConflict);
        }

        OnPropertyChanged(nameof(HasFindings));
    }

    /// <summary>
    /// Marks the rows whose devices are in the plan. Called after a scan and after anything that
    /// changes the plan, so the list does not invite you to add the same device twice.
    /// </summary>
    public void ApplyPlan(Func<MacAddress, bool> isPlanned)
    {
        ArgumentNullException.ThrowIfNull(isPlanned);

        foreach (ScanResultViewModel row in Rows)
        {
            row.IsAlreadyPlanned = !row.Mac.IsEmpty && isPlanned(row.Mac);
        }
    }

    public void Clear()
    {
        Rows.Clear();
        SelectedRow = null;
        Status = "Cleared.";

        Conformance = PlanConformanceReport.None;
        Findings.Clear();

        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(HasFindings));
    }
}
