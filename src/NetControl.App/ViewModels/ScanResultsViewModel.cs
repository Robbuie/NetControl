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
        "No scan yet. Scan asks every EtherNet/IP device on the selected adapter to identify itself, and "
        + "can ping every address on the subnet to find everything else.";

    /// <summary>
    /// Whether Scan also sweeps the subnet: one echo request to every address, then a read of the
    /// ARP cache. On by default, because "what is on this network" is what somebody pressing Scan
    /// nearly always means - and it is ICMP only, paced, with no connection opened to anything.
    /// </summary>
    [ObservableProperty]
    private bool _sweepSubnet = true;

    /// <summary>False when this build has no sweep, or the selected adapter's subnet cannot be swept.</summary>
    [ObservableProperty]
    private bool _canSweep;

    /// <summary>
    /// The sweep checkbox's caption: which subnet, how many addresses, roughly how long - or why the
    /// selected adapter cannot be swept. Made from the same plan the sweep itself would use.
    /// </summary>
    [ObservableProperty]
    private string _sweepCaption = "Also ping every address on the subnet";

    /// <summary>The longer explanation, for the checkbox's tooltip.</summary>
    [ObservableProperty]
    private string? _sweepTooltip;

    /// <summary>
    /// Subnets beyond this adapter's own to sweep, as typed: "10.20.5.0/24, 10.20.6.10-50". Kept with
    /// the project, because they are the site's. Checked as it is typed, so a mistake shows before
    /// the button is pressed rather than after.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RoutedRangesProblem))]
    [NotifyPropertyChangedFor(nameof(RoutedRangesNote))]
    [NotifyPropertyChangedFor(nameof(ParsedRoutedRanges))]
    private string _routedRanges = string.Empty;

    /// <summary>False in a build with no sweep, where there is nothing to do with a range.</summary>
    [ObservableProperty]
    private bool _canRoute;

    /// <summary>The widest one range may be - the same cap a subnet sweep has.</summary>
    public const int MaxRangeAddresses = 1024;

    /// <summary>The most routed addresses one scan will ping, all ranges together.</summary>
    public const int MaxRoutedAddresses = 4096;

    /// <summary>The ranges as parsed, or empty while <see cref="RoutedRangesProblem"/> says why not.</summary>
    public IReadOnlyList<SweepRange> ParsedRoutedRanges =>
        ParseRoutedRanges(out IReadOnlyList<SweepRange> ranges) is null ? ranges : [];

    /// <summary>Why the ranges cannot be used, naming each piece that is wrong. Null when they can.</summary>
    public string? RoutedRangesProblem => ParseRoutedRanges(out _);

    /// <summary>One line under the box: how much a scan will add, or nothing when the box is empty.</summary>
    public string? RoutedRangesNote
    {
        get
        {
            if (RoutedRangesProblem is not null)
            {
                return null;
            }

            IReadOnlyList<SweepRange> ranges = ParsedRoutedRanges;
            return ranges.Count == 0
                ? null
                : FormattableString.Invariant(
                    $"{ranges.Count} range(s), {ranges.Sum(r => r.Count):N0} addresses, pinged and then asked to identify.")
                    + " Behind a router there are no MAC addresses, and devices that ignore ping are not seen.";
        }
    }

    private string? ParseRoutedRanges(out IReadOnlyList<SweepRange> ranges)
    {
        if (!SweepRange.TryParseList(RoutedRanges, MaxRangeAddresses, out ranges, out string? problem))
        {
            return problem;
        }

        int total = ranges.Sum(r => r.Count);
        return total > MaxRoutedAddresses
            ? string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"Those ranges come to {total:N0} addresses, more than the {MaxRoutedAddresses:N0} one scan will ping. Narrow them, or scan in parts.")
            : null;
    }

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

    /// <summary>
    /// What changed since the last scan of the same subnet, worst first. Empty after a first scan of
    /// a subnet, which has nothing to be compared with.
    /// </summary>
    public ObservableCollection<InventoryChange> Changes { get; } = [];

    /// <summary>The comparison's one sentence, or null when there was nothing to compare with.</summary>
    [ObservableProperty]
    private string? _changesSummary;

    public bool HasResults => Rows.Count > 0;

    public bool HasFindings => Findings.Count > 0;

    public bool HasChanges => Changes.Count > 0;

    /// <summary>
    /// Shows what changed since the previous scan of this subnet, or clears it when there was no
    /// previous scan. See <see cref="InventoryDiff"/>.
    /// </summary>
    public void ApplyChanges(InventoryDiffResult? diff)
    {
        Changes.Clear();

        if (diff is not null)
        {
            foreach (InventoryChange change in diff.Changes)
            {
                Changes.Add(change);
            }
        }

        ChangesSummary = diff?.Summary;
        OnPropertyChanged(nameof(HasChanges));
    }

    /// <summary>Replaces the list with what a scan found.</summary>
    public void Apply(DiscoveryResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        Rows.Clear();

        foreach (DiscoveredDevice device in result.Devices.OrderBy(d => AddressKey(d.Address)))
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
    /// Empties the list for a new scan and says what is happening. Each pass of the scan then adds
    /// to it - <see cref="Merge"/> and <see cref="ApplySweep"/> - so an adapter with two addresses,
    /// and the routed ranges after them, end up in one list.
    /// </summary>
    public void Begin(string status)
    {
        Rows.Clear();
        SelectedRow = null;
        Status = status;
        ApplyConformance(PlanConformanceReport.None);
        ApplyChanges(null);
        OnPropertyChanged(nameof(HasResults));
    }

    /// <summary>
    /// Adds what an identity pass found. A device at an address a sweep already put on screen takes
    /// that row over and keeps its ping result, so an address is never two rows.
    /// </summary>
    public void Merge(DiscoveryResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var rows = Rows.ToList();

        foreach (DiscoveredDevice device in result.Devices)
        {
            ScanResultViewModel? sweptOnly = rows.FirstOrDefault(r => r.Device is null
                && (r.Address.Equals(device.Address) || r.Address.Equals(device.SenderAddress)));

            var row = new ScanResultViewModel(device);

            if (sweptOnly is not null)
            {
                row.Host = sweptOnly.Host;
                rows.Remove(sweptOnly);
            }

            rows.Add(row);
        }

        Replace(rows);
    }

    private void Replace(List<ScanResultViewModel> rows)
    {
        ScanResultViewModel? selected = SelectedRow;
        Rows.Clear();

        foreach (ScanResultViewModel row in rows.OrderBy(r => AddressKey(r.Address)))
        {
            Rows.Add(row);
        }

        SelectedRow = selected is not null && Rows.Contains(selected) ? selected : null;
        OnPropertyChanged(nameof(HasResults));
    }

    /// <summary>
    /// Joins a subnet sweep onto the rows the identity scan put on screen: an address that answered
    /// both is one row carrying both, and everything that only the sweep found gets a row of its own.
    /// The list ends up in address order as a number, so it reads top to bottom the way a panel does.
    /// </summary>
    public void ApplySweep(SubnetSweepResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var rows = Rows.ToList();

        foreach (SweptHost host in result.Hosts)
        {
            // Matched on the address the identity reply came from as well as the one it claims:
            // the sweep pinged the address that is really answering, which is the sender.
            ScanResultViewModel? existing = rows.FirstOrDefault(r => r.Device is { } d
                && (d.Address.Equals(host.Address) || d.SenderAddress.Equals(host.Address)));

            if (existing is not null)
            {
                existing.Host = host;
            }
            else
            {
                rows.Add(new ScanResultViewModel(host));
            }
        }

        Replace(rows);
    }

    /// <summary>An IPv4 address as a number, so .2 sorts above .100.</summary>
    private static uint AddressKey(System.Net.IPAddress address)
    {
        byte[] b = address.GetAddressBytes();
        return b.Length == 4 ? ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3] : uint.MaxValue;
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
                        && f.Address.Equals(row.Address))
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
        ApplyChanges(null);

        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(HasFindings));
    }
}
