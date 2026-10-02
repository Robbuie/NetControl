using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetControl.App.Composition;
using NetControl.App.Diagnostics;
using NetControl.App.Serving;
using NetControl.Core;
using NetControl.Core.Dhcp;
using NetControl.Core.Interfaces;
using NetControl.Core.Oui;
using NetControl.Core.Persistence;
using NetControl.Core.Tftp;

namespace NetControl.App.ViewModels;

/// <summary>
/// The TFTP tab: is this PC ready to see a robot's image backup, did the robot ask for its file,
/// and - across both listeners - which step of the backup it stopped at.
///
/// <para>PLAN-TFTP.md parts E1, E3 (Observe) and E5, as they appear in the app. Everything here was
/// reachable only through <c>spikes/Spike3.TftpWatch</c> before.</para>
///
/// <para><b>The two ports are graded separately and never averaged.</b> UDP/67 held by another
/// program while NetControl wants to serve is a conflict; UDP/69 held by the TFTP server on the
/// backup server is the system working. The same fact is good news on one row and bad on the
/// other, so this tab keeps its own checks rather than adding rows to the interface bar, and the
/// UDP/69 grade depends on whether the user says this PC <em>is</em> the backup server.</para>
///
/// <para><b>Refusing is a transmission</b>, so the watch is armed separately from being started,
/// exactly as Serve is, and it only ever answers on the adapter selected at the top of the window.
/// A request arriving anywhere else is recorded and left alone.</para>
/// </summary>
public sealed partial class TftpViewModel : ObservableObject, IDisposable
{
    /// <summary>Enough for a long bench session; past it the oldest rows go, and the project file keeps them.</summary>
    private const int MaxRows = 500;

    /// <summary>How far back a retransmit looks for the row it belongs to. Retries arrive within seconds.</summary>
    private const int RetransmitSearchDepth = 50;

    private readonly IUiDispatcher _dispatcher;
    private readonly IPreflight _preflight;
    private readonly TftpWatchController _watch;
    private readonly Func<ProjectStore> _project;
    private readonly Func<AdapterOption?> _selectedAdapter;
    private readonly OuiDatabase _oui;
    private readonly TimeProvider _time;
    private readonly int _listenPort;

    // Evidence for the verdict, from both listeners. Cleared together by Clear.
    private readonly Dictionary<MacAddress, int> _attempts = [];
    private readonly Dictionary<MacAddress, IPAddress> _served = [];
    private ServerRunState _dhcpState = ServerRunState.Stopped;
    private DhcpServerMode _dhcpMode = DhcpServerMode.Watch;
    private int _addressRequests;
    private MacAddress? _requester;
    private TftpRequestEventArgs? _fileRequest;
    private int _fileRequests;

    private PortConflictReport? _lastPort;
    private bool _disposed;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartWatchCommand))]
    private bool _isWatchArmed;

    /// <summary>
    /// Which vantage this is. On the backup server, nothing holding UDP/69 is the fault; on a laptop
    /// on the robot network it is what lets the watch bind. Nothing can work that out reliably, so
    /// the user says - and the root folder check only means anything when this is true.
    /// </summary>
    [ObservableProperty]
    private bool _isBackupServer;

    [ObservableProperty]
    private string _rootFolder = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartWatchCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopWatchCommand))]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    private ServerRunState _runState = ServerRunState.Stopped;

    [ObservableProperty]
    private ReadinessCheck _portCheck;

    [ObservableProperty]
    private ReadinessCheck _firewallCheck;

    [ObservableProperty]
    private ReadinessCheck _rootCheck;

    [ObservableProperty]
    private ReadinessCheck _listenerCheck;

    [ObservableProperty]
    private IReadOnlyList<ReadinessCheck> _checks = [];

    /// <summary>The four steps of the backup, in the order the controller takes them.</summary>
    [ObservableProperty]
    private IReadOnlyList<ReadinessCheck> _steps = [];

    [ObservableProperty]
    private ReadinessState _overall = ReadinessState.Unknown;

    /// <summary>The one sentence: the earliest step not known to have worked.</summary>
    [ObservableProperty]
    private string _verdict = string.Empty;

    [ObservableProperty]
    private string? _verdictRemediation;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _errorRemediation;

    public TftpViewModel(
        IUiDispatcher dispatcher,
        IPreflight preflight,
        TftpWatchController watch,
        Func<ProjectStore> project,
        Func<AdapterOption?> selectedAdapter,
        OuiDatabase oui,
        TimeProvider? timeProvider = null,
        int listenPort = TftpLimits.ServerPort)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(preflight);
        ArgumentNullException.ThrowIfNull(watch);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(selectedAdapter);
        ArgumentNullException.ThrowIfNull(oui);

        _dispatcher = dispatcher;
        _preflight = preflight;
        _watch = watch;
        _project = project;
        _selectedAdapter = selectedAdapter;
        _oui = oui;
        _time = timeProvider ?? TimeProvider.System;
        _listenPort = listenPort;

        string port = PortText;
        _portCheck = ReadinessCheck.NotChecked($"UDP/{port}", "Not checked yet.", "Press Re-check.");
        _firewallCheck = ReadinessCheck.NotChecked("Firewall", "Not checked yet.", "Press Re-check.");
        _rootCheck = GradeRoot(null);
        _listenerCheck = GradeListener(ServerRunState.Stopped);

        _watch.RequestReceived += OnRequestReceived;
        _watch.Fault += OnFault;
        _watch.RecordingFailed += OnRecordingFailed;
        _watch.StateChanged += OnWatchStateChanged;

        Recompute();
    }

    /// <summary>Newest first, like the request log, so nothing has to auto-scroll.</summary>
    public ObservableCollection<TftpRequestRowViewModel> Requests { get; } = [];

    public bool IsRunning => RunState is ServerRunState.Listening or ServerRunState.Starting;

    private string PortText => _listenPort.ToString(CultureInfo.InvariantCulture);

    /// <summary>The selected adapter changed at the top of the window; the watch answers only on it.</summary>
    public void NoteAdapterChanged() => StartWatchCommand.NotifyCanExecuteChanged();

    /// <summary>The BOOTP/DHCP listener started, stopped or faulted - step 1 can only be seen while it runs.</summary>
    public void NoteDhcpState(ServerRunState state, DhcpServerMode mode)
    {
        _dhcpState = state;
        _dhcpMode = mode;
        Recompute();
    }

    /// <summary>
    /// A BOOTP/DHCP request arrived. Called on the UI thread by the window's view model, which
    /// already marshals the DHCP listener's events.
    ///
    /// <para>The verdict follows one device. A FANUC is preferred over anything else that happens to
    /// be asking on the same segment, because that is the device whose backup this tab is about;
    /// otherwise it is whoever asked most recently.</para>
    /// </summary>
    public void NoteDhcpRequest(DhcpRequestEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        _addressRequests++;
        _attempts[e.Mac] = _attempts.TryGetValue(e.Mac, out int seen) ? seen + 1 : 1;

        if (_requester is not { } current || !IsFanuc(current) || IsFanuc(e.Mac))
        {
            _requester = e.Mac;
        }

        Recompute();
    }

    /// <summary>NetControl answered a request. Only a reply carrying an address counts as step 2.</summary>
    public void NoteDhcpReply(DhcpReplyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (e.Assignment is not { } assignment)
        {
            return;
        }

        _served[e.Mac] = assignment.Ip;

        // The device that was just served is the one the verdict is about, whatever else is asking.
        _requester = e.Mac;
        Recompute();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _watch.RequestReceived -= OnRequestReceived;
        _watch.Fault -= OnFault;
        _watch.RecordingFailed -= OnRecordingFailed;
        _watch.StateChanged -= OnWatchStateChanged;
        _watch.Dispose();
    }

    /// <summary>Who holds UDP/69 and whether the firewall lets it in - plus the folder, if one is given.</summary>
    [RelayCommand]
    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            PreflightResult result = await _preflight.InspectAsync(_listenPort, cancellationToken)
                .ConfigureAwait(true);

            _lastPort = result.Port;
            PortCheck = GradePort(result.Port);
            FirewallCheck = InterfaceBarViewModel.GradeFirewall(result.Firewall);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(RootFolder))
        {
            await CheckRootAsync().ConfigureAwait(true);
        }

        Recompute();
    }

    /// <summary>
    /// Writes a probe file into the folder and removes it again - see <see cref="TftpRootCheck"/>
    /// for why asking the ACL is not the same question. Off the UI thread: the folder may be on a
    /// share that takes its time to answer.
    /// </summary>
    [RelayCommand]
    private async Task CheckRootAsync()
    {
        string folder = RootFolder;
        TftpRootStatus status = await Task.Run(() => TftpRootCheck.Inspect(folder)).ConfigureAwait(true);

        // The user may have typed a different path while the check ran; a grade for the old one
        // would be shown against the new one.
        if (string.Equals(folder, RootFolder, StringComparison.Ordinal))
        {
            RootCheck = GradeRoot(status);
            Recompute();
        }
    }

    [RelayCommand(CanExecute = nameof(CanStartWatch))]
    private async Task StartWatchAsync()
    {
        ClearError();

        if (_selectedAdapter() is not { } adapter)
        {
            SetError(
                "No adapter is selected.",
                "The watch answers a request only on the adapter selected at the top of the window, and "
                    + "it is never chosen for you.");
            return;
        }

        var options = new TftpWatchOptions
        {
            ListenPort = _listenPort,
            Mode = TftpWatchMode.Observe,

            // Requests on every adapter are recorded; only those on the selected one are refused,
            // because a refusal is a transmission and this tool does not transmit onto a segment
            // nobody chose. See TftpWatchServer.
            InterfaceIndexFilter = adapter.Index,
        };

        try
        {
            await _watch.StartAsync(options, _project()).ConfigureAwait(true);
            AddRow(TftpRequestRowViewModel.Notice(
                _time.GetUtcNow(),
                $"Watching UDP/{PortText}. Requests on {adapter.DisplayName} are recorded and refused; "
                    + "requests on any other adapter are recorded only."));
        }
        catch (TftpBindException ex)
        {
            SetError(ex.Message, ex.Remediation);
            AddRow(TftpRequestRowViewModel.Notice(_time.GetUtcNow(), ex.Message, ex.Remediation));
        }
        catch (InvalidOperationException ex)
        {
            SetError(ex.Message, null);
        }

        // Who holds the port has just changed, one way or the other.
        await RefreshAsync(CancellationToken.None).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanStopWatch))]
    private async Task StopWatchAsync()
    {
        await _watch.StopAsync().ConfigureAwait(true);
        IsWatchArmed = false;
        AddRow(TftpRequestRowViewModel.Notice(
            _time.GetUtcNow(),
            $"TFTP watch stopped. Start the real TFTP server again before the next backup."));
        await RefreshAsync(CancellationToken.None).ConfigureAwait(true);
    }

    /// <summary>Starts a fresh attempt: empties the list and forgets what both listeners had seen.</summary>
    [RelayCommand]
    private void Clear()
    {
        Requests.Clear();
        _attempts.Clear();
        _served.Clear();
        _addressRequests = 0;
        _requester = null;
        _fileRequest = null;
        _fileRequests = 0;
        ClearError();
        Recompute();
    }

    private bool CanStartWatch() =>
        RunState is ServerRunState.Stopped or ServerRunState.Faulted
        && IsWatchArmed
        && _selectedAdapter() is not null;

    private bool CanStopWatch() => RunState is ServerRunState.Listening;

    partial void OnIsBackupServerChanged(bool value)
    {
        if (_lastPort is { } report)
        {
            PortCheck = GradePort(report);
        }

        RootCheck = GradeRoot(null);
        Recompute();
    }

    partial void OnRootFolderChanged(string value)
    {
        RootCheck = GradeRoot(null);
        Recompute();
    }

    private void OnRequestReceived(object? sender, TftpRequestEventArgs e) => RunOnUi(() => NoteFileRequest(e));

    /// <summary>
    /// A file request reached the watch. Internal, rather than inlined into the handler, so the tests
    /// can drive the list and the verdict with a hand-built request and no socket - the same reason
    /// the recorders do it.
    /// </summary>
    internal void NoteFileRequest(TftpRequestEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        _fileRequests++;

        // A write request is what a backup sends, so once one has been seen it stays the subject of
        // the verdict; a read request only takes the place of nothing or of another read.
        if (_fileRequest is null || e.Request.IsWrite || !_fileRequest.Request.IsWrite)
        {
            _fileRequest = e;
        }

        if (e.IsRetransmit && FindRow(e) is { } row)
        {
            row.RepeatCount++;
        }
        else
        {
            AddRow(TftpRequestRowViewModel.FromRequest(e));
        }

        Recompute();
    }

    private void OnFault(object? sender, TftpFaultEventArgs e) => RunOnUi(() =>
    {
        AddRow(TftpRequestRowViewModel.FromFault(e));
        if (e.IsFatal)
        {
            SetError(e.Message, e.Remediation);
        }
    });

    private void OnRecordingFailed(object? sender, PersistenceFailedEventArgs e) => RunOnUi(() =>
        AddRow(TftpRequestRowViewModel.Notice(
            _time.GetUtcNow(),
            $"Could not record the {e.What}: {e.Message}",
            "The request is still in this list. The project file's record of this session is incomplete.")));

    private void OnWatchStateChanged(object? sender, EventArgs e) => RunOnUi(() =>
    {
        RunState = _watch.State;
        ListenerCheck = GradeListener(RunState);

        if (RunState is ServerRunState.Faulted && _watch.StopReason is { } reason)
        {
            SetError(reason, "Press Watch TFTP to start it again.");
        }

        Recompute();
    });

    private TftpRequestRowViewModel? FindRow(TftpRequestEventArgs e)
    {
        int depth = Math.Min(Requests.Count, RetransmitSearchDepth);
        for (int i = 0; i < depth; i++)
        {
            if (Requests[i].IsSameRequestAs(e))
            {
                return Requests[i];
            }
        }

        return null;
    }

    private void AddRow(TftpRequestRowViewModel row)
    {
        Requests.Insert(0, row);
        while (Requests.Count > MaxRows)
        {
            Requests.RemoveAt(Requests.Count - 1);
        }
    }

    private ReadinessCheck GradePort(PortConflictReport report)
    {
        string name = $"UDP/{PortText}";

        if (report.Owners.Count == 0 && report.Summary.StartsWith("Could not determine", StringComparison.Ordinal))
        {
            return ReadinessCheck.NotChecked(name, report.Summary);
        }

        int self = Environment.ProcessId;
        UdpPortOwner? other = report.Owners.FirstOrDefault(o => o.ProcessId != self);

        if (other is null && report.Owners.Count > 0)
        {
            return new ReadinessCheck(name, ReadinessState.Ready, $"NetControl's own watch holds UDP/{PortText}.");
        }

        if (other is not null)
        {
            string who = $"{other.ProcessName} (pid {other.ProcessId.ToString(CultureInfo.InvariantCulture)})";
            return IsBackupServer
                ? new ReadinessCheck(
                    name,
                    ReadinessState.Ready,
                    $"{who} holds UDP/{PortText} - the TFTP server is running on this PC.",
                    "To watch the controller's request with NetControl, stop it first: the watch will not "
                        + "share the port. Start it again afterwards.")
                : new ReadinessCheck(
                    name,
                    ReadinessState.Warning,
                    $"{who} holds UDP/{PortText}, so the watch cannot bind.",
                    "If it is a TFTP server left over from testing, stop it. If this PC is the backup "
                        + "server, tick 'This PC is the backup server'.");
        }

        return IsBackupServer
            ? new ReadinessCheck(
                name,
                ReadinessState.Blocked,
                $"Nothing holds UDP/{PortText} - no TFTP server is running on this PC, so a backup sent "
                    + "here has nothing to answer it.",
                "Start the TFTP server. If it runs as a service, it may have stopped or failed to start "
                    + "when the PC booted.")
            : new ReadinessCheck(name, ReadinessState.Ready, $"UDP/{PortText} is free - the watch can bind.");
    }

    private ReadinessCheck GradeRoot(TftpRootStatus? status)
    {
        const string Name = "Backup folder";

        if (string.IsNullOrWhiteSpace(RootFolder))
        {
            return ReadinessCheck.NotChecked(
                Name,
                IsBackupServer ? "No folder given." : "Only checked on the backup server.",
                "On the backup server, point this at the TFTP server's root folder: it is checked for "
                    + "existing, being writable, and having room for an image.");
        }

        if (status is null)
        {
            return ReadinessCheck.NotChecked(Name, "Not checked yet.", "Press Check folder.");
        }

        ReadinessState state = status.Verdict switch
        {
            TftpRootVerdict.Writable => ReadinessState.Ready,
            TftpRootVerdict.LowSpace => ReadinessState.Warning,
            TftpRootVerdict.NotWritable or TftpRootVerdict.Missing => ReadinessState.Blocked,
            _ => ReadinessState.Unknown,
        };

        return new ReadinessCheck(Name, state, status.Summary, status.Remediation);
    }

    private ReadinessCheck GradeListener(ServerRunState state) => state switch
    {
        ServerRunState.Listening => new ReadinessCheck(
            "Watch",
            ReadinessState.Ready,
            $"Watching UDP/{PortText}. Requests are recorded and refused, so nothing is received."),
        ServerRunState.Starting => ReadinessCheck.NotChecked("Watch", "Binding the socket..."),
        ServerRunState.Stopping => ReadinessCheck.NotChecked("Watch", "Stopping..."),
        ServerRunState.Faulted => new ReadinessCheck(
            "Watch",
            ReadinessState.Blocked,
            _watch.StopReason ?? "The TFTP watch stopped because of a fault.",
            "Press Watch TFTP to start it again."),
        _ => ReadinessCheck.NotChecked(
            "Watch",
            "Not started.",
            "Stop the real TFTP server, tick Arm, then press Watch TFTP."),
    };

    private void Recompute()
    {
        Checks = [PortCheck, FirewallCheck, RootCheck, ListenerCheck];

        int attempts = _requester is { } mac && _attempts.TryGetValue(mac, out int n) ? n : 0;
        IPAddress? served = _requester is { } who && _served.TryGetValue(who, out IPAddress? address)
            ? address
            : null;

        BackupVerdictResult result = BackupVerdict.Evaluate(new BackupEvidence
        {
            DhcpState = _dhcpState,
            DhcpMode = _dhcpMode,
            AddressRequests = _addressRequests,
            Requester = _requester is { } requester ? Describe(requester) : null,
            RequesterAttempts = attempts,
            ServedAddress = served,
            ServerPort = PortCheck,
            Firewall = FirewallCheck,
            WatchState = RunState,
            WatchStopReason = _watch.StopReason,
            FileRequest = _fileRequest,
            FileRequests = _fileRequests,
        });

        Steps = result.Steps;
        Overall = result.Overall;
        Verdict = result.Headline;
        VerdictRemediation = result.Remediation;
    }

    private string Describe(MacAddress mac) =>
        _oui.Lookup(mac) is { } vendor ? $"{mac} ({vendor})" : mac.ToString();

    private bool IsFanuc(MacAddress mac) =>
        _oui.Lookup(mac) is { } vendor && vendor.Contains("FANUC", StringComparison.OrdinalIgnoreCase);

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
