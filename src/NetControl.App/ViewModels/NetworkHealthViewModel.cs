using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetControl.App.Composition;
using NetControl.Core;
using NetControl.Core.Analysis;
using NetControl.Core.Interfaces;
using NetControl.Core.Persistence;
using NetControl.Core.Plan;
using NetControl.Core.Reachability;
using NetControl.Core.Safety;

namespace NetControl.App.ViewModels;

/// <summary>
/// The Network health tab: Troubleshoot (one button, every check, a ranked answer), the address
/// conflict watch, and the ping monitor.
///
/// <para>Nothing here starts by itself. A view model is constructed by every test that builds a
/// <see cref="MainViewModel"/>, and the tab is a place somebody goes when something is wrong - so
/// every check, the ARP watch and the monitor run only when asked.</para>
/// </summary>
public sealed partial class NetworkHealthViewModel : ObservableObject, IDisposable
{
    /// <summary>How often the ARP watch reads this PC's ARP table. Local - nothing is sent.</summary>
    public static readonly TimeSpan ArpWatchInterval = TimeSpan.FromSeconds(5);

    private readonly IUiDispatcher _dispatcher;
    private readonly Func<ProjectStore> _project;
    private readonly Func<NicInfo?> _adapter;
    private readonly Func<IReadOnlyList<NicInfo>> _adapters;
    private readonly DeviceGridViewModel _plan;
    private readonly Func<int> _unplannedAskers;
    private readonly Func<TrafficSnapshot?> _traffic;
    private readonly IArpLookup _arp;
    private readonly NetworkGuard _guard;
    private readonly TroubleshootRunner _runner;
    private readonly TimeProvider _time;

    private CancellationTokenSource? _troubleshoot;
    private CancellationTokenSource? _arpWatch;
    private bool _refreshQueued;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunTroubleshootCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelTroubleshootCommand))]
    private bool _isTroubleshooting;

    [ObservableProperty]
    private bool _includePingChecks = true;

    [ObservableProperty]
    private string? _troubleshootStatus = "Runs every check this tool can make, in the order a person would walk the path - this PC, its cable, addresses, devices, traffic - and lists what is wrong first.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ArpWatchText))]
    private bool _isWatchingArp;

    [ObservableProperty]
    private string? _conflictStatus;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddTargetCommand))]
    private string _pingTargetText = string.Empty;

    [ObservableProperty]
    private string _intervalText = "1000";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveTargetCommand))]
    private PingTargetRowViewModel? _selectedTarget;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartMonitorCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopMonitorCommand))]
    private bool _isMonitoring;

    [ObservableProperty]
    private string? _monitorStatus;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _errorRemediation;

    public NetworkHealthViewModel(
        IUiDispatcher dispatcher,
        Func<ProjectStore> project,
        Func<NicInfo?> adapter,
        Func<IReadOnlyList<NicInfo>> adapters,
        DeviceGridViewModel plan,
        Func<int> unplannedAskers,
        Func<TrafficSnapshot?> traffic,
        IPinger pinger,
        IArpLookup arp,
        NetworkGuard? guard = null,
        TimeProvider? timeProvider = null,
        TroubleshootRunner? runner = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(adapters);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(unplannedAskers);
        ArgumentNullException.ThrowIfNull(traffic);
        ArgumentNullException.ThrowIfNull(pinger);
        ArgumentNullException.ThrowIfNull(arp);

        _dispatcher = dispatcher;
        _project = project;
        _adapter = adapter;
        _adapters = adapters;
        _plan = plan;
        _unplannedAskers = unplannedAskers;
        _traffic = traffic;
        _arp = arp;
        _guard = guard ?? NetworkGuard.Shared;
        _time = timeProvider ?? TimeProvider.System;
        _runner = runner ?? new TroubleshootRunner(pinger, arp, _guard, _time);

        Detector = new AddressConflictDetector(timeProvider: _time);
        Detector.ConflictDetected += OnConflictDetected;

        Monitor = new PingMonitor(pinger, _time);
        Monitor.Updated += (_, _) => QueueRefresh();
        Monitor.StateChanged += (_, e) => _dispatcher.Post(() => RecordOutage(e.Address, e.Down, e.Message));
        Monitor.Stopped += (_, message) => _dispatcher.Post(() =>
        {
            IsMonitoring = false;
            MonitorStatus = message;
        });

        _plan.SelectionChanged += (_, _) => AddSelectedPlanRowsCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Fed by the ARP watch here and by ARP frames on the Passive tab.</summary>
    public AddressConflictDetector Detector { get; }

    public PingMonitor Monitor { get; }

    public ObservableCollection<NetworkFinding> Findings { get; } = [];

    public ObservableCollection<ConflictRowViewModel> Conflicts { get; } = [];

    public ObservableCollection<PingTargetRowViewModel> Targets { get; } = [];

    /// <summary>The last troubleshoot's findings, for the support bundle. Null until one has run.</summary>
    public IReadOnlyList<NetworkFinding>? LastFindings { get; private set; }

    public string ArpWatchText => IsWatchingArp
        ? $"Reading this PC's ARP table every {ArpWatchInterval.TotalSeconds:0} s. Nothing is sent."
        : "Off. Reads this PC's ARP table on a timer - nothing is sent. Listening on the Passive tab also feeds this list, and hears far more.";

    // ----------------------------------------------------------------- Troubleshoot

    [RelayCommand(CanExecute = nameof(CanRunTroubleshoot))]
    private async Task RunTroubleshootAsync()
    {
        ClearError();
        IsTroubleshooting = true;
        Findings.Clear();
        _troubleshoot = new CancellationTokenSource();

        try
        {
            NicInfo? nic = _adapter();
            var request = new TroubleshootRequest
            {
                Adapter = nic,
                AllAdapters = _adapters(),
                Plan = PlannedAddresses(),
                Conflicts = Detector.Conflicts(),
                Traffic = _traffic(),
                UnplannedAskers = _unplannedAskers(),
                IncludeActiveChecks = IncludePingChecks,
            };

            if (nic is not null)
            {
                Detector.SetOwnAddresses(nic.PerAddress().Where(a => a.IPv4 is not null).Select(a => (a.IPv4!, nic.Mac)));
            }

            var progress = new Progress<string>(text => TroubleshootStatus = text);
            (TroubleshootInputs inputs, IReadOnlyList<NetworkFinding> findings) =
                await _runner.RunAsync(request, progress, _troubleshoot.Token).ConfigureAwait(true);

            LastFindings = findings;
            foreach (NetworkFinding finding in findings)
            {
                Findings.Add(finding);
            }

            int problems = findings.Count(f => f.Severity == FindingSeverity.Error);
            int checks = findings.Count(f => f.Severity == FindingSeverity.Warn);
            string pinged = inputs.ActiveChecksRan
                ? string.Create(CultureInfo.CurrentCulture, $", {inputs.PlanPings.Count} planned device(s) pinged.")
                : ", no pings sent.";
            TroubleshootStatus = string.Create(
                CultureInfo.CurrentCulture,
                $"Finished {_time.GetLocalNow():HH:mm:ss}: {problems} problem(s), {checks} thing(s) to check") + pinged;

            // A read that transmits gets a row too - and the verdict is worth having in the record.
            Record(
                problems > 0 ? EventSeverity.Warn : EventSeverity.Info,
                inputs.ActiveChecksRan ? EventCategory.Scan : EventCategory.App,
                $"Troubleshoot: {problems} problem(s), {checks} to check"
                    + (findings.FirstOrDefault(f => f.Severity >= FindingSeverity.Warn) is { } worst ? $"; first: {worst.Title}." : "."));
        }
        catch (OperationCanceledException)
        {
            TroubleshootStatus = "Cancelled.";
        }
        finally
        {
            _troubleshoot.Dispose();
            _troubleshoot = null;
            IsTroubleshooting = false;
        }
    }

    private bool CanRunTroubleshoot() => !IsTroubleshooting;

    [RelayCommand(CanExecute = nameof(IsTroubleshooting))]
    private void CancelTroubleshoot() => _troubleshoot?.Cancel();

    /// <summary>The findings as plain text, for the clipboard or an email.</summary>
    public string FindingsText()
    {
        var text = new StringBuilder();
        text.AppendLine(TroubleshootStatus);

        foreach (NetworkFinding f in Findings)
        {
            text.AppendLine(CultureInfo.CurrentCulture, $"[{f.SeverityText}] {f.Area}: {f.Title}");
            if (!string.IsNullOrWhiteSpace(f.Detail))
            {
                text.AppendLine("    " + f.Detail);
            }

            if (f.NextStep is { } next)
            {
                text.AppendLine("    Next: " + next);
            }
        }

        return text.ToString();
    }

    // ----------------------------------------------------------------- Address conflicts

    partial void OnIsWatchingArpChanged(bool value)
    {
        if (value)
        {
            StartArpWatch();
        }
        else
        {
            _arpWatch?.Cancel();
            _arpWatch?.Dispose();
            _arpWatch = null;
        }
    }

    private async void StartArpWatch()
    {
        if (!_arp.IsSupported)
        {
            ConflictStatus = "This PC's ARP table cannot be read here.";
            IsWatchingArp = false;
            return;
        }

        _arpWatch = new CancellationTokenSource();
        CancellationToken token = _arpWatch.Token;

        try
        {
            using var timer = new PeriodicTimer(ArpWatchInterval, _time);
            do
            {
                ReadArpOnce();
            }
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(true));
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
    }

    /// <summary>One read of the ARP table into the detector. Internal so a test can drive it without a timer.</summary>
    internal void ReadArpOnce()
    {
        NicInfo? nic = _adapter();
        if (nic is not null)
        {
            Detector.SetOwnAddresses(nic.PerAddress().Where(a => a.IPv4 is not null).Select(a => (a.IPv4!, nic.Mac)));
        }

        try
        {
            Detector.ObserveArpTable(_arp.Snapshot(), nic?.Index);
        }
        catch (Exception ex) when (ex is NetControlException or InvalidOperationException)
        {
            ConflictStatus = $"Could not read the ARP table: {ex.Message}";
            return;
        }

        RefreshConflicts();
    }

    [RelayCommand]
    private void ClearConflicts()
    {
        Detector.Clear();
        RefreshConflicts();
    }

    internal void RefreshConflicts()
    {
        IReadOnlyList<AddressConflict> conflicts = Detector.Conflicts();
        Conflicts.Clear();

        foreach (AddressConflict conflict in conflicts)
        {
            Conflicts.Add(new ConflictRowViewModel(conflict));
        }

        ConflictStatus = conflicts.Count == 0
            ? string.Create(CultureInfo.CurrentCulture, $"No address claimed by two devices. {Detector.AddressesTracked} address(es) watched, {Detector.Observations:N0} sighting(s).")
            : string.Create(CultureInfo.CurrentCulture, $"{conflicts.Count} contested address(es).");
    }

    private void OnConflictDetected(object? sender, AddressConflict conflict) => _dispatcher.Post(() =>
    {
        NetworkFinding finding = conflict.ToFinding();
        Record(finding.Severity == FindingSeverity.Error ? EventSeverity.Warn : EventSeverity.Info, EventCategory.Scan,
            $"{finding.Title}: {conflict.MacsText}.", conflict.Address.ToString());
        RefreshConflicts();
    });

    // ----------------------------------------------------------------- Ping monitor

    [RelayCommand(CanExecute = nameof(CanAddTarget))]
    private void AddTarget()
    {
        ClearError();

        if (!PlanValidation.TryParseIPv4(PingTargetText.Trim(), out IPAddress? address) || address is null)
        {
            SetError($"'{PingTargetText.Trim()}' is not an IPv4 address.", "Type four numbers separated by dots, e.g. 192.168.1.51.");
            return;
        }

        if (Monitor.Add(address, NameFor(address), MaskFor(address)) is { } problem)
        {
            SetError(problem, null);
            return;
        }

        PingTargetText = string.Empty;
        RefreshTargets();
    }

    private bool CanAddTarget() => !string.IsNullOrWhiteSpace(PingTargetText);

    [RelayCommand(CanExecute = nameof(CanAddSelectedPlanRows))]
    private void AddSelectedPlanRows()
    {
        ClearError();
        var problems = new List<string>();

        foreach (DeviceRowViewModel row in _plan.Selection)
        {
            if (!PlanValidation.TryParseIPv4(row.IpText.Trim(), out IPAddress? address) || address is null)
            {
                continue;
            }

            if (Monitor.Add(address, RowName(row), MaskFor(address)) is { } problem
                && !problem.Contains("already", StringComparison.Ordinal))
            {
                problems.Add(problem);
            }
        }

        if (problems.Count > 0)
        {
            SetError(problems[0], problems.Count > 1 ? $"And {problems.Count - 1} more." : null);
        }

        RefreshTargets();
    }

    private bool CanAddSelectedPlanRows() => _plan.Selection.Count > 0;

    [RelayCommand(CanExecute = nameof(CanRemoveTarget))]
    private void RemoveTarget()
    {
        if (SelectedTarget is { } target)
        {
            Monitor.Remove(target.Address);
            RefreshTargets();
        }
    }

    private bool CanRemoveTarget() => SelectedTarget is not null;

    [RelayCommand(CanExecute = nameof(CanStartMonitor))]
    private void StartMonitor()
    {
        ClearError();

        if (Monitor.Count == 0)
        {
            SetError("Add an address to monitor first.", "Type one above, or select rows in the plan and press Add selected plan rows.");
            return;
        }

        if (_guard.ListenOnly)
        {
            SetError("Listen-only mode is on, so the ping monitor would send nothing.", "Turn Listen only off to monitor.");
            return;
        }

        if (!int.TryParse(IntervalText.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out int ms) || ms < PingMonitor.MinimumInterval.TotalMilliseconds)
        {
            ms = (int)PingMonitor.MinimumInterval.TotalMilliseconds;
            IntervalText = ms.ToString(CultureInfo.CurrentCulture);
        }

        Monitor.Start(TimeSpan.FromMilliseconds(ms));
        IsMonitoring = true;
        MonitorStatus = string.Create(CultureInfo.CurrentCulture, $"Monitoring {Monitor.Count} address(es) every {ms} ms. One echo each per round, at most {PingMonitor.MaxInFlight} at once.");
        Record(EventSeverity.Info, EventCategory.Scan, $"Ping monitor started on {string.Join(", ", Targets.Select(t => t.AddressText))}, every {ms} ms.");
    }

    private bool CanStartMonitor() => !IsMonitoring;

    [RelayCommand(CanExecute = nameof(IsMonitoring))]
    private async Task StopMonitorAsync()
    {
        await Monitor.StopAsync().ConfigureAwait(true);
        IsMonitoring = false;
        RefreshTargets();

        string summary = string.Join("; ", Monitor.Snapshot().Select(s => $"{s.Address} {s.LossText}, outages {s.OutageText}"));
        MonitorStatus = "Stopped. " + summary;
        Record(EventSeverity.Info, EventCategory.Scan, "Ping monitor stopped: " + summary);
    }

    [RelayCommand]
    private void ResetMonitor()
    {
        Monitor.Reset();
        RefreshTargets();
    }

    /// <summary>Brings the rows in line with the monitor, updating in place.</summary>
    internal void RefreshTargets()
    {
        _refreshQueued = false;
        IReadOnlyList<PingTargetStats> snapshot = Monitor.Snapshot();

        for (int i = Targets.Count - 1; i >= 0; i--)
        {
            if (!snapshot.Any(s => s.Address.Equals(Targets[i].Address)))
            {
                Targets.RemoveAt(i);
            }
        }

        foreach (PingTargetStats stats in snapshot)
        {
            PingTargetRowViewModel? row = Targets.FirstOrDefault(t => t.Address.Equals(stats.Address));
            if (row is null)
            {
                Targets.Add(new PingTargetRowViewModel(stats));
            }
            else
            {
                row.Stats = stats;
            }
        }
    }

    /// <summary>Coalesces round updates: at most one UI refresh queued at a time, however fast the rounds.</summary>
    private void QueueRefresh()
    {
        if (_refreshQueued)
        {
            return;
        }

        _refreshQueued = true;
        _dispatcher.Post(RefreshTargets);
    }

    private void RecordOutage(IPAddress address, bool down, string message)
    {
        Record(down ? EventSeverity.Warn : EventSeverity.Info, EventCategory.Scan, message, address.ToString());
        MonitorStatus = message;
    }

    // ----------------------------------------------------------------- Plumbing

    public void Dispose()
    {
        _troubleshoot?.Cancel();
        _arpWatch?.Cancel();
        _arpWatch?.Dispose();
        _arpWatch = null;

        // Fire and forget is safe here: StopAsync cancels a loop that only awaits its own timer.
        _ = Monitor.StopAsync();
    }

    private List<PlannedAddress> PlannedAddresses() => [.. _plan.Rows.Select(row => new PlannedAddress(
        RowName(row),
        PlanValidation.TryParseIPv4(row.IpText.Trim(), out IPAddress? ip) ? ip : null,
        PlanValidation.TryParseIPv4(row.MaskText.Trim(), out IPAddress? mask) ? mask : null,
        row.Mac.IsEmpty ? null : row.Mac))];

    private static string RowName(DeviceRowViewModel row) =>
        new[] { row.Role, row.HostName, row.PanelRef, row.IpText }.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))?.Trim() ?? "device";

    private string? NameFor(IPAddress address) =>
        _plan.Rows.FirstOrDefault(r => r.IpText.Trim() == address.ToString()) is { } row ? RowName(row) : null;

    /// <summary>The adapter's mask when the address is on its subnet - so the subnet's broadcast is refused too.</summary>
    private IPAddress? MaskFor(IPAddress address)
    {
        if (_adapter() is not { } nic)
        {
            return null;
        }

        foreach (NicInfo a in nic.PerAddress())
        {
            if (Ipv4Subnet.TryCreate(a.IPv4, a.Mask, out Ipv4Subnet subnet) && subnet.Contains(address))
            {
                return a.Mask;
            }
        }

        return null;
    }

    private void Record(EventSeverity severity, EventCategory category, string message, string? target = null)
    {
        try
        {
            _project().Events.Append(severity, category, message, target: target);
        }
        catch (PersistenceException ex)
        {
            SetError($"Could not record that in the project: {ex.Message}", ex.Remediation);
        }
    }

    private void SetError(string message, string? remediation)
    {
        ErrorMessage = message;
        ErrorRemediation = remediation;
    }

    private void ClearError()
    {
        ErrorMessage = null;
        ErrorRemediation = null;
    }
}
