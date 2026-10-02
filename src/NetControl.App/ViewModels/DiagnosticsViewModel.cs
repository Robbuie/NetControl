using System.Collections.ObjectModel;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetControl.App.Composition;
using NetControl.Core;
using NetControl.Core.DeviceHealth;
using NetControl.Core.Enip;
using NetControl.Core.Interfaces;
using NetControl.Core.Persistence;
using NetControl.Core.Plan;
using NetControl.Core.Reachability;

namespace NetControl.App.ViewModels;

/// <summary>
/// The Diagnostics tab: one device, three questions - does anything answer at its address, what
/// does it say about its own health, and which services does it offer - plus pinging every planned
/// device at once from the grid.
///
/// <para><b>Everything here is a read, and every read is unicast to one address somebody named.</b>
/// The address comes from a plan row, a scan result, or the box on the tab; it is checked through
/// <see cref="UnicastTarget"/> before anything leaves the PC. Nothing here writes to a device, and
/// the CIP read is Get_Attribute_Single and nothing else - see <see cref="DeviceHealthReader"/>.</para>
///
/// <para><b>Every transmission still gets an event row.</b> A read is not a state change, but it is
/// packets on somebody's plant network, and the record is where "who was poking at the drives on
/// Tuesday" gets answered. Ping and the service check are filed under Scan, alongside discovery; the
/// health read is filed under Cip, alongside the commissioning it so often follows.</para>
///
/// <para>One operation at a time. A ping sweep and a CIP read interleaved in the log read as one
/// confusing operation, and nothing is gained by running them at once.</para>
/// </summary>
public sealed partial class DiagnosticsViewModel : ObservableObject
{
    private readonly IUiDispatcher _dispatcher;
    private readonly Func<ProjectStore> _project;
    private readonly DeviceGridViewModel _plan;
    private readonly IPinger _pinger;
    private readonly PingSweepOptions _pingOptions;
    private readonly DeviceHealthReader _healthReader;
    private readonly ServiceProbe _serviceProbe;
    private readonly Func<NicInfo?> _selectedAdapter;
    private readonly Func<int, NicCounters?> _readNicCounters;

    /// <summary>The last read of this PC's adapter, so the next can say what moved.</summary>
    private NicCounters? _previousNicCounters;

    /// <summary>
    /// The mask that goes with the target when it came from the plan, so a planned directed
    /// broadcast is refused as well as the addresses that are never one host. Cleared when the
    /// address is typed by hand: a mask that belonged to a different address proves nothing.
    /// </summary>
    private IPAddress? _targetMask;

    /// <summary>
    /// The last health read, kept so the next read of the same device can say what moved in between.
    /// "37 late collisions since power-up" and "37 in the last minute" are different findings.
    /// </summary>
    private DeviceHealthReport? _previousHealth;

    /// <summary>Set while a target is being filled in from outside, so it does not clear its own mask.</summary>
    private bool _settingTarget;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PingTargetCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReadHealthCommand))]
    [NotifyCanExecuteChangedFor(nameof(CheckServicesCommand))]
    private string _targetText = string.Empty;

    /// <summary>What the plan calls the target, when it came from the plan. Shown beside the address.</summary>
    [ObservableProperty]
    private string? _targetName;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PingTargetCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReadHealthCommand))]
    [NotifyCanExecuteChangedFor(nameof(CheckServicesCommand))]
    [NotifyCanExecuteChangedFor(nameof(PingPlanCommand))]
    private bool _isBusy;

    /// <summary>One line saying what the last operation found, or what is happening now.</summary>
    [ObservableProperty]
    private string? _status;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _errorRemediation;

    /// <summary>The last ping of the target, as a sentence.</summary>
    [ObservableProperty]
    private string? _pingResult;

    /// <summary>The last health read: identity line, network line and the comparison note.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHealth))]
    private DeviceHealthResult? _health;

    [ObservableProperty]
    private string? _healthComparison;

    /// <summary>The device's own web page, when the service check found one.</summary>
    [ObservableProperty]
    private Uri? _webPage;

    /// <summary>This PC's own adapter counters, as a sentence. Null until somebody reads them.</summary>
    [ObservableProperty]
    private string? _adapterCountersText;

    /// <summary>True when the last two reads of this PC's adapter found new errors - its own cable.</summary>
    [ObservableProperty]
    private bool _adapterHasNewErrors;

    public DiagnosticsViewModel(
        IUiDispatcher dispatcher,
        Func<ProjectStore> project,
        DeviceGridViewModel plan,
        IPinger? pinger = null,
        PingSweepOptions? pingOptions = null,
        DeviceHealthReader? healthReader = null,
        ServiceProbe? serviceProbe = null,
        Func<NicInfo?>? selectedAdapter = null,
        Func<int, NicCounters?>? readNicCounters = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(plan);

        _dispatcher = dispatcher;
        _project = project;
        _plan = plan;
        _pinger = pinger ?? new IcmpPinger();
        _pingOptions = pingOptions ?? new PingSweepOptions();
        _healthReader = healthReader ?? new DeviceHealthReader();
        _serviceProbe = serviceProbe ?? new ServiceProbe();
        _selectedAdapter = selectedAdapter ?? (() => null);
        _readNicCounters = readNicCounters ?? (index => NicCounters.Read(index));
    }

    /// <summary>Findings from the last health read, worst first.</summary>
    public ObservableCollection<HealthFinding> Findings { get; } = [];

    /// <summary>The ports from the last health read.</summary>
    public ObservableCollection<LinkPortRowViewModel> Ports { get; } = [];

    /// <summary>Notes from the last health read: what the device would not say.</summary>
    public ObservableCollection<string> HealthNotes { get; } = [];

    /// <summary>The last service check, one row per port asked about.</summary>
    public ObservableCollection<ServiceCheck> Services { get; } = [];

    public bool HasHealth => Health is not null;

    /// <summary>"1756-EN2T/D rev 11.03 serial 00C0FFEE at 192.168.1.51".</summary>
    public string? HealthTitle => Health?.Report.Title;

    /// <summary>The device's own account of its address, method and status word.</summary>
    public string? HealthNetworkText
    {
        get
        {
            if (Health?.Report is not { } report)
            {
                return null;
            }

            var parts = new List<string>();

            if (report.Configuration is { } configuration)
            {
                parts.Add(configuration.ToString());
            }

            if (report.Method is { } method)
            {
                parts.Add($"method {method}");
            }

            if (report.HostName is { Length: > 0 } host)
            {
                parts.Add($"host name '{host}'");
            }

            if (report.TcpIpStatus is { } tcpIp)
            {
                parts.Add(tcpIp.ToString());
            }

            if (report.Status is { } status)
            {
                parts.Add($"device status: {status}");
            }

            return parts.Count == 0 ? "The device did not describe its network settings." : string.Join("; ", parts);
        }
    }

    /// <summary>
    /// Points the tab at one device without sending anything. Called from the plan and the scan
    /// results; the buttons on the tab are what transmit.
    /// </summary>
    /// <param name="address">The device.</param>
    /// <param name="mask">Its mask, when known - lets a planned directed broadcast be refused too.</param>
    /// <param name="name">What the plan calls it, if anything.</param>
    public void SetTarget(IPAddress address, IPAddress? mask = null, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(address);

        _settingTarget = true;

        try
        {
            TargetText = address.ToString();
            _targetMask = mask;
            TargetName = name;
        }
        finally
        {
            _settingTarget = false;
        }

        ClearError();
    }

    /// <summary>Pings every planned address and fills in the grid's Reach column as answers arrive.</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task PingPlanAsync(CancellationToken cancellationToken)
    {
        // Address to rows: two rows planning one address is a plan problem, but if it is there the
        // answer belongs on both rows rather than on whichever happened to be first.
        var rowsByAddress = new Dictionary<IPAddress, List<DeviceRowViewModel>>();
        var targets = new List<PingTarget>();

        foreach (DeviceRowViewModel row in _plan.Rows)
        {
            row.Reach = null;

            if (!row.TryBuild(out DeviceRecord? record) || record?.PlannedIp is not { } ip)
            {
                continue;
            }

            if (!rowsByAddress.TryGetValue(ip, out List<DeviceRowViewModel>? rows))
            {
                rows = [];
                rowsByAddress[ip] = rows;
                targets.Add(new PingTarget(ip, record.PlannedMask));
            }

            rows.Add(row);
        }

        if (targets.Count == 0)
        {
            SetError("Nothing in the plan has an address to ping.", "Type planned addresses into the grid first.");
            return;
        }

        var sweep = new PingSweep(_pinger, _pingOptions);
        sweep.Progress += (_, e) => RunOnUi(() =>
        {
            if (rowsByAddress.TryGetValue(e.Outcome.Address, out List<DeviceRowViewModel>? rows))
            {
                foreach (DeviceRowViewModel row in rows)
                {
                    row.Reach = e.Outcome;
                }
            }

            Status = $"Pinging the plan: {e.Completed} of {e.Total}...";
        });

        await RunAsync(
            $"Pinging {targets.Count} planned address(es)...",
            async () =>
            {
                PingSweepResult result = await sweep.RunAsync(targets, cancellationToken).ConfigureAwait(true);

                Status = result.Summary;
                Record(
                    result.Severity,
                    EventCategory.Scan,
                    $"Ping of the plan: {result.Summary}",
                    target: null,
                    new EventDetail()
                        .Add("addresses", result.Outcomes.Count)
                        .Add("replied", result.Replied)
                        .Add("silent", result.Silent)
                        .Add("unreachable", result.Unreachable));
            }).ConfigureAwait(true);
    }

    /// <summary>Pings the target, up to the configured number of attempts.</summary>
    [RelayCommand(CanExecute = nameof(CanRunOnTarget))]
    private async Task PingTargetAsync(CancellationToken cancellationToken)
    {
        if (!TryTarget(out IPAddress? address))
        {
            return;
        }

        var sweep = new PingSweep(_pinger, _pingOptions);

        await RunAsync(
            $"Pinging {address}...",
            async () =>
            {
                PingSweepResult result = await sweep
                    .RunAsync([new PingTarget(address!, _targetMask)], cancellationToken)
                    .ConfigureAwait(true);

                PingOutcome outcome = result.Outcomes[0];
                PingResult = outcome.Sentence;
                Status = outcome.Sentence;

                Record(
                    result.Severity,
                    EventCategory.Scan,
                    $"Ping: {outcome.Sentence}",
                    address!.ToString(),
                    new EventDetail()
                        .Add("status", outcome.Status)
                        .Add("attempts", outcome.Attempts)
                        .Add("roundTripMs", outcome.RoundTrip?.TotalMilliseconds.ToString(
                            System.Globalization.CultureInfo.InvariantCulture)));
            }).ConfigureAwait(true);
    }

    /// <summary>
    /// Reads the device's identity, TCP/IP interface and every port over CIP. Read-only. A second
    /// read of the same device compares the counters with the first.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunOnTarget))]
    private async Task ReadHealthAsync(CancellationToken cancellationToken)
    {
        if (!TryTarget(out IPAddress? address))
        {
            return;
        }

        await RunAsync(
            $"Reading diagnostics from {address} over EtherNet/IP...",
            async () =>
            {
                DeviceHealthReport report = await _healthReader
                    .ReadAsync(address!, _targetMask, cancellationToken)
                    .ConfigureAwait(true);

                DeviceHealthReport? previous =
                    _previousHealth is { } earlier && earlier.Address.Equals(address) ? earlier : null;

                DeviceHealthResult result = DeviceHealthAssessment.Assess(report, previous);
                _previousHealth = report;

                ShowHealth(result, previous);
                Status = result.Summary;

                Record(
                    result.Severity,
                    EventCategory.Cip,
                    $"Diagnostics read: {result.Summary}",
                    address!.ToString(),
                    new EventDetail()
                        .Add("operation", "readDiagnostics")
                        .Add("product", report.ProductName)
                        .Add("serial", report.SerialText)
                        .Add("ports", report.Ports.Count)
                        .Add("errors", result.Errors)
                        .Add("warnings", result.Warnings)
                        .Add("comparedWithEarlierRead", previous is not null));
            }).ConfigureAwait(true);
    }

    /// <summary>Connects to each of a short list of TCP ports, one at a time, and closes at once.</summary>
    [RelayCommand(CanExecute = nameof(CanRunOnTarget))]
    private async Task CheckServicesAsync(CancellationToken cancellationToken)
    {
        if (!TryTarget(out IPAddress? address))
        {
            return;
        }

        Services.Clear();
        WebPage = null;

        void OnChecked(object? sender, ServiceCheck check) => RunOnUi(() =>
        {
            Services.Add(check);
            Status = $"Checking services on {address}: {Services.Count} of {_serviceProbe.Ports.Count}...";
        });

        _serviceProbe.Checked += OnChecked;

        try
        {
            await RunAsync(
                $"Checking {_serviceProbe.Ports.Count} TCP services on {address}, one at a time...",
                async () =>
                {
                    ServiceProbeResult result = await _serviceProbe
                        .RunAsync(address!, _targetMask, cancellationToken)
                        .ConfigureAwait(true);

                    WebPage = result.WebPage;
                    Status = result.Summary;

                    Record(
                        EventSeverity.Info,
                        EventCategory.Scan,
                        $"Service check: {result.Summary}",
                        address!.ToString(),
                        new EventDetail()
                            .Add("ports", result.Checks.Count)
                            .Add("open", string.Join(",", result.Open.Select(c => c.Service.Port))));
                }).ConfigureAwait(true);
        }
        finally
        {
            _serviceProbe.Checked -= OnChecked;
        }
    }

    /// <summary>
    /// Reads the selected adapter's own error counters - this PC's half of the link. Nothing is sent;
    /// it is a question to Windows, so it is not recorded as a transmission.
    /// </summary>
    [RelayCommand]
    private void ReadAdapterCounters()
    {
        if (_selectedAdapter() is not { } nic)
        {
            AdapterCountersText = "Select an adapter at the top of the window first.";
            AdapterHasNewErrors = false;
            return;
        }

        NicCounters? now = _readNicCounters(nic.Index);

        if (now is null)
        {
            AdapterCountersText = $"Windows did not report counters for [{nic.Index}] {nic.Name} - it may have just been removed.";
            AdapterHasNewErrors = false;
            return;
        }

        NicCounters? earlier = _previousNicCounters is { } previous && previous.Index == now.Index ? previous : null;

        AdapterCountersText = now.Describe(earlier);
        AdapterHasNewErrors = earlier is not null && now.HasNewErrorsSince(earlier);
        _previousNicCounters = now;
    }

    partial void OnTargetTextChanged(string value)
    {
        if (_settingTarget)
        {
            return;
        }

        // Typed by hand: whatever mask and name came with the last target belonged to a different
        // address, and carrying them over would refuse or label the wrong thing.
        _targetMask = null;
        TargetName = null;
    }

    partial void OnHealthChanged(DeviceHealthResult? value)
    {
        OnPropertyChanged(nameof(HealthTitle));
        OnPropertyChanged(nameof(HealthNetworkText));
    }

    private bool CanRun() => !IsBusy;

    private bool CanRunOnTarget() => !IsBusy && !string.IsNullOrWhiteSpace(TargetText);

    private void ShowHealth(DeviceHealthResult result, DeviceHealthReport? previous)
    {
        Health = result;

        Findings.Clear();
        foreach (HealthFinding finding in result.Findings)
        {
            Findings.Add(finding);
        }

        Ports.Clear();
        foreach (LinkPort port in result.Report.Ports)
        {
            Ports.Add(new LinkPortRowViewModel(port));
        }

        HealthNotes.Clear();
        foreach (string note in result.Report.Notes)
        {
            HealthNotes.Add(note);
        }

        HealthComparison = previous is null
            ? "First read of this device in this session: counters are totals since they were last cleared. "
                + "Read again in a minute to see which are still moving."
            : $"Counters compared with the read at {previous.ReadUtc.ToLocalTime():HH:mm:ss}.";
    }

    /// <summary>Parses the box, saying what is wrong with it when it does not parse.</summary>
    private bool TryTarget(out IPAddress? address)
    {
        if (PlanValidation.TryParseIPv4(TargetText.Trim(), out address)
            && UnicastTarget.TryCheck(address, _targetMask, out string? problem))
        {
            return true;
        }

        if (address is null)
        {
            SetError(
                $"'{TargetText.Trim()}' is not an IPv4 address.",
                "Type four numbers, e.g. 192.168.1.51 - or pick the device in the plan or the scan results.");
        }
        else
        {
            UnicastTarget.TryCheck(address, _targetMask, out problem);
            SetError(problem ?? $"{address} is not one device.", "Probes go to one device at a time.");
        }

        address = null;
        return false;
    }

    /// <summary>
    /// Runs one operation with the busy flag, the status line and the error banner handled the same
    /// way for all of them. The failures caught here are the ones Core raises on purpose, each
    /// already worded with its cause and next action.
    /// </summary>
    private async Task RunAsync(string starting, Func<Task> work)
    {
        ClearError();
        IsBusy = true;
        Status = starting;

        try
        {
            await work().ConfigureAwait(true);
        }
        catch (ReachabilityException ex)
        {
            SetError(ex.Message, ex.Remediation);
            Status = ex.Message;
        }
        catch (EnipException ex)
        {
            SetError(ex.Message, ex.Remediation);
            Status = ex.Message;
            Record(EventSeverity.Warn, EventCategory.Cip, $"Diagnostics read failed: {ex.Message}", TargetText.Trim());
        }
        catch (OperationCanceledException)
        {
            // The window is closing. Nothing was written to anything, and nothing worth saying.
            Status = "Cancelled.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// One line of the record. Never throws: a project file that has gone read-only must not turn a
    /// completed read into an error.
    /// </summary>
    private void Record(
        EventSeverity severity, EventCategory category, string message, string? target, EventDetail? detail = null)
    {
        try
        {
            _project().Events.Append(severity, category, message, target: target, detail: detail);
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

    private void RunOnUi(Action action)
    {
        if (_dispatcher.IsOnUiThread)
        {
            action();
            return;
        }

        _dispatcher.Post(action);
    }
}
