// UseWPF drops System.IO from the implicit usings, because WPF ships System.Windows.Shapes.Path
// and the SDK will not let two Path types collide. Save As needs File, so this file asks for it
// back explicitly rather than the whole project doing so.
using System.IO;
using System.Net;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetControl.App.Composition;
using NetControl.App.Diagnostics;
using NetControl.App.Serving;
using NetControl.Core;
using NetControl.Core.Commissioning;
using NetControl.Core.Dhcp;
using NetControl.Core.Discovery;
using NetControl.Core.Enip;
using NetControl.Core.Interfaces;
using NetControl.Core.Oui;
using NetControl.Core.Persistence;
using NetControl.Core.Tftp;

// Shares a name with this type's Plan property, which is the grid. Nothing here refers to the
// namespace by its bare name - a simple name inside the class binds to the member - so the two
// coexist, but it is worth knowing before adding a type called Plan to either.
using NetControl.Core.Plan;

namespace NetControl.App.ViewModels;

/// <summary>
/// The window's view model: it owns the project, the plan the server serves from, and the
/// listener's lifetime, and it is the only place in the app where a Core event is marshalled onto
/// the UI thread.
///
/// <para><b>Serve mode is explicit, and it is explicit twice.</b> The user has to select an
/// adapter that can actually source a reply, and then arm serving deliberately
/// (<see cref="IsServeArmed"/>). A rogue DHCP server on a plant network is a genuinely serious
/// incident, so handing out addresses is never something that happens because a plan file
/// happened to be open.</para>
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IUiDispatcher _dispatcher;
    private readonly INicInventory _nics;
    private readonly IPreflight _preflight;
    private readonly ServerController _server;
    private readonly StaticMapPolicy _policy;

    /// <summary>
    /// The plan as the log sees it: every row, not just the servable ones. Rebuilt alongside
    /// <see cref="_policy"/> in <see cref="ReloadPlan"/> - see <see cref="PlanIndex"/> for why the
    /// two are separate.
    /// </summary>
    private readonly PlanIndex _planIndex = new();

    /// <summary>
    /// Held here as well as handed to the sub-view-models, because a CSV import resolves the vendor
    /// for every MAC once, at import, and stores it on the row. See <see cref="PlanImportContext"/>.
    /// </summary>
    private readonly OuiDatabase _oui;

    private readonly TimeProvider _time;
    private readonly int _listenPort;

    /// <summary>
    /// Active discovery. Null when the app was built without one, which is how a test that has no
    /// business scanning gets a view model whose scan button is simply unavailable.
    /// </summary>
    private readonly DeviceDiscovery? _discovery;

    /// <summary>
    /// The last scan, kept only so the comparison against the plan can be re-run when the plan is
    /// edited underneath it. The devices are a snapshot and stay one - what moves is the plan half
    /// of the comparison, and a finding about an address the user has just corrected should stop
    /// saying so.
    /// </summary>
    private DiscoveryResult? _lastScan;

    /// <summary>
    /// What the update check found, or null when there is nothing worth saying - which is both the
    /// ordinary states: no manifest URL configured, and already on the current build. Set by the
    /// host; the view model does not reach the network.
    /// </summary>
    [ObservableProperty]
    private string? _updateStatus;

    /// <summary>
    /// One line naming the build, written into every project this session touches. Null in tests
    /// and anywhere else that has no version to claim - see the note where the app passes it.
    /// </summary>
    private readonly string? _buildStamp;

    private ProjectStore _project;
    private bool _disposed;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartServeCommand))]
    private bool _isServeArmed;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartWatchCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartServeCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    private ServerRunState _runState = ServerRunState.Stopped;

    [ObservableProperty]
    private string _projectName = "Untitled";

    [ObservableProperty]
    private string? _projectPath;

    /// <summary>
    /// True while the project has never been given a file.
    ///
    /// <para>Not a dirty flag - there is no such thing here. SQLite writes as the plan is typed, so
    /// a project can only ever be missing a <em>location</em>, never its contents. That is worth
    /// saying on screen, because everyone arrives expecting a Save button and a prompt on exit.</para>
    /// </summary>
    [ObservableProperty]
    private bool _isUnsavedLocation;

    [ObservableProperty]
    private int _plannedDeviceCount;

    /// <summary>
    /// The last thing that went wrong, shown as a banner. Cleared by the next successful action,
    /// because a stale error beside a working tool is worse than no error at all.
    /// </summary>
    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _errorRemediation;

    /// <summary>
    /// True once a row has failed to reach the project file. The UI stops claiming to be
    /// recording: a commissioning record with a silent hole in it is worse than one that admits it.
    /// </summary>
    [ObservableProperty]
    private bool _recordingIsIncomplete;

    /// <summary>
    /// Whether replies are steered with a short-lived socket bound to the adapter's own address,
    /// rather than with IP_UNICAST_IF on the listening socket.
    ///
    /// <para>Defaults to the per-socket bind, and deliberately so. Phase 0 established that
    /// IP_UNICAST_IF steers a broadcast correctly to a listener <em>on the same machine</em>;
    /// whether a device out on the wire receives it has never been proved, so until it is, the app
    /// picks the mechanism whose routing decision is unambiguous. Core's own default is the other
    /// way round because Core has no opinion about hardware it has not met. The toggle exists so
    /// the question can be settled on a bench with a real panel, which is the only place it can be.</para>
    /// </summary>
    [ObservableProperty]
    private bool _usePerSocketBind = true;

    public MainViewModel(
        IUiDispatcher dispatcher,
        INicInventory nics,
        IPreflight preflight,
        ServerController server,
        StaticMapPolicy policy,
        OuiDatabase oui,
        ProjectStore project,
        TimeProvider? timeProvider = null,
        int listenPort = 67,
        DeviceDiscovery? discovery = null,
        string? buildStamp = null,
        TftpWatchController? tftpWatch = null,
        TftpSettings? tftpSettings = null,
        Action<TftpSettings>? saveTftpSettings = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(nics);
        ArgumentNullException.ThrowIfNull(preflight);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(oui);
        ArgumentNullException.ThrowIfNull(project);

        _dispatcher = dispatcher;
        _nics = nics;
        _preflight = preflight;
        _server = server;
        _policy = policy;
        _oui = oui;
        _project = project;
        _time = timeProvider ?? TimeProvider.System;
        _listenPort = listenPort;
        _discovery = discovery;
        _buildStamp = buildStamp;

        InterfaceBar = new InterfaceBarViewModel(oui, _time, listenPort);

        // The TFTP tab. Its own controller, because an image backup is a BOOTP request followed by
        // a TFTP request and both listeners have to run at once. It reads the project and the
        // selected adapter through these two lambdas rather than holding either, because both
        // change under it - File, Open swaps the project, and the adapter is the user's to change.
        Tftp = new TftpViewModel(
            dispatcher,
            preflight,
            tftpWatch ?? new TftpWatchController(nics, _time),
            () => _project,
            () => InterfaceBar.SelectedAdapter,
            oui,
            _time,
            TftpLimits.ServerPort,
            settings: tftpSettings,
            saveSettings: saveTftpSettings);
        Log = new RequestLogViewModel(oui, _planIndex, _time);
        Plan = new DeviceGridViewModel(oui);

        InterfaceBar.PropertyChanged += OnInterfaceBarPropertyChanged;

        // The grid writes to the file; this is what carries the edit through to the dictionary the
        // receive loop reads. Planning a device while watching it ask, and having it served on its
        // next retransmit, is the whole loop this tool exists for.
        Plan.PlanChanged += OnPlanChanged;

        // Commissioning runs off the UI thread and narrates itself as it goes, because it writes to
        // live equipment and takes seconds. Same marshalling rule as the server's events.
        Plan.CommissionProgress += OnCommissionProgress;

        _server.RequestReceived += OnRequestReceived;
        _server.ReplySent += OnReplySent;
        _server.Fault += OnFault;
        _server.RecordingFailed += OnRecordingFailed;
        _server.StateChanged += OnServerStateChanged;

        ApplyProject(_project);

        RefreshAdapters();
    }

    public InterfaceBarViewModel InterfaceBar { get; }

    public RequestLogViewModel Log { get; }

    /// <summary>The plan, editable. Writes straight through to the project file as cells are left.</summary>
    public DeviceGridViewModel Plan { get; }

    /// <summary>What the last scan found. Empty, and harmless, until somebody presses Scan.</summary>
    public ScanResultsViewModel Scan { get; } = new();

    /// <summary>The TFTP tab: the UDP/69 watch, and which step of an image backup failed.</summary>
    public TftpViewModel Tftp { get; }

    /// <summary>
    /// Which build this is, in the status bar. It is here because it is the first question anybody
    /// asks about a copied exe, and because the project file records the same string - so a record
    /// and the tool in front of you can be matched without opening either.
    /// </summary>
    public string VersionText => $"v{NetControl.App.Diagnostics.BuildInfo.Version}";

    public bool IsRunning => RunState is ServerRunState.Listening or ServerRunState.Starting;

    /// <summary>Re-reads the adapter inventory. Safe to call from any thread.</summary>
    public void RefreshAdapters()
    {
        IReadOnlyList<NicInfo> snapshot = _nics.Snapshot();
        RunOnUi(() => InterfaceBar.ApplyInventory(snapshot));
    }

    /// <summary>
    /// Closes the current project and opens the one at <paramref name="path"/>, loading its plan.
    /// The listener must be stopped first: switching the file a running server records into would
    /// tear a commissioning record in half.
    /// </summary>
    public void OpenProject(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!RequireStopped("opening another project"))
        {
            return;
        }

        try
        {
            // SchemaVersionException derives from PersistenceException, so a file written by a
            // newer build lands in the same catch - with the "update the tool, this build will not
            // downgrade it" remediation already written by Core.
            Swap(ProjectStore.Open(path, timeProvider: _time));
            Log.AddNotice($"Opened {ProjectName} ({ProjectPath}).");
        }
        catch (PersistenceException ex)
        {
            SetError(ex.Message, ex.Remediation);
        }
    }

    /// <summary>
    /// Starts over on a fresh in-memory project, exactly as the app starts. Nothing is written
    /// until it is saved somewhere - and, as at startup, the event log is recording from the first
    /// second rather than from whenever somebody picks a filename.
    /// </summary>
    public void NewProject()
    {
        if (!RequireStopped("starting a new project"))
        {
            return;
        }

        Swap(ProjectStore.CreateInMemory(timeProvider: _time));
        Log.Clear();
        Log.AddNotice("New project. It is not saved anywhere yet - use File, Save as.");
    }

    /// <summary>
    /// Gives the project a home on disk, carrying the plan, what has been served, and the whole
    /// event log with it, then reopens it there so later writes land in the file.
    ///
    /// <para><paramref name="replaceExisting"/> exists because Core refuses to write over a file
    /// that is already there. The confirmation belongs to whoever put the dialog on the screen: a
    /// project file is a commissioning record, and quietly replacing one is not undoable.</para>
    /// </summary>
    public void SaveProjectAs(string path, bool replaceExisting = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!RequireStopped("saving to another file"))
        {
            return;
        }

        try
        {
            if (replaceExisting && File.Exists(path))
            {
                File.Delete(path);
            }

            Swap(_project.SaveAs(path));
            Log.AddNotice($"Saved as {ProjectPath}. Every later edit is written there as you make it.");
        }
        catch (PersistenceException ex)
        {
            SetError(ex.Message, ex.Remediation);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetError(
                $"Could not replace '{path}': {ex.Message}",
                "Close anything that has the file open, or save under a different name.");
        }
    }

    /// <summary>
    /// Reads a plan CSV and, if the whole file is good, writes all of it into the project.
    ///
    /// <para><b>Nothing is imported if anything is wrong.</b> Every problem is reported with its
    /// line number and the plan is left exactly as it was - see <see cref="PlanValidation"/>. Half a
    /// panel planned is worse than none, because it looks finished.</para>
    ///
    /// <para>The listener has to be stopped. Bringing in a file swaps a whole set of mappings at
    /// once, and doing that underneath a running server is not an edit, it is a different job - the
    /// A6 rule. Adding one device while serving stays allowed, and is the normal workflow.</para>
    ///
    /// <para>Rows are matched to the plan by MAC and replace what is there, so re-importing a
    /// corrected file corrects the plan rather than doubling it. The vendor is resolved from the
    /// MAC here and stored, which is why the file has no vendor column.</para>
    /// </summary>
    public void ImportPlan(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!RequireStopped("importing a plan"))
        {
            return;
        }

        IReadOnlyList<PlanCsvRow> rows;
        try
        {
            rows = PlanCsv.Read(File.ReadAllText(path));
        }
        catch (PlanCsvException ex)
        {
            SetError(ex.Message, ex.Remediation);
            RecordPlanFile(EventSeverity.Warn, $"Plan import refused: {ex.Message}", path, imported: 0);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetError($"Could not read '{path}': {ex.Message}", "Check the file is there and not open elsewhere.");
            return;
        }

        PlanValidationResult result = PlanValidation.Validate(rows, ImportContext());

        if (!result.IsValid)
        {
            SetError(
                result.Summary,
                "The plan has not been changed. Every problem is listed in the log, with the line it is on.");

            foreach (PlanProblem problem in result.Problems)
            {
                Log.AddNotice($"Import: {problem}");
            }

            RecordPlanFile(EventSeverity.Warn, $"Plan import refused: {result.Summary}", path, imported: 0);
            return;
        }

        try
        {
            _project.Devices.UpsertRange(result.Devices);
        }
        catch (PersistenceException ex)
        {
            SetError(ex.Message, ex.Remediation);
            return;
        }

        ClearError();
        Plan.Reload();
        ReloadPlan();
        Log.AddNotice($"Imported {result.Devices.Count} device(s) from {path}.");
        RecordPlanFile(EventSeverity.Info, result.Summary, path, result.Devices.Count);
    }

    /// <summary>
    /// Writes the plan out as CSV: the same columns import reads, so a file exported here can be
    /// edited in a spreadsheet and brought back.
    ///
    /// <para>It exports the plan, not the commissioning record. What was served and what was
    /// verified live in the project file's append-only event log, and a CSV that carried them would
    /// be a record somebody could edit.</para>
    /// </summary>
    public void ExportPlan(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            IReadOnlyList<DeviceRecord> devices = _project.Devices.All();

            // UTF-8 with a byte order mark: it is what a spreadsheet expects, and without it Excel
            // reads a non-ASCII vendor name as mojibake. PlanCsv.Read skips one on the way back in.
            File.WriteAllText(path, PlanCsv.Write(devices), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            ClearError();
            Log.AddNotice($"Exported {devices.Count} device(s) to {path}.");
            RecordPlanFile(EventSeverity.Info, $"Plan exported: {devices.Count} device(s).", path, devices.Count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetError(
                $"Could not write '{path}': {ex.Message}",
                "Close the file if a spreadsheet has it open, or export under a different name.");
        }
    }

    /// <summary>
    /// Moves a device from the live log into the plan: the loop this whole tool is built around.
    /// You watch a device ask, you plan it, and it is served on its next retransmit.
    ///
    /// <para>The MAC and the vendor come across; the address does not, because the tool does not
    /// have an opinion about what a device should be called. See
    /// <see cref="DeviceGridViewModel.PlanDevice"/>.</para>
    ///
    /// <para>Nothing is transmitted by this. Adding a row to the plan makes a device answerable in
    /// Serve mode; it does not start serving, and it does not arm anything.</para>
    /// </summary>
    public void PlanFromLog(LogEntryViewModel entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (entry.Mac.IsEmpty)
        {
            // A notice, a fault, or the tool talking about itself. Saying so beats a double-click
            // that appears to do nothing at all.
            Log.AddNotice("That row is not about a device, so there is no MAC to add to the plan.");
            return;
        }

        bool wasAlreadyPlanned = Plan.IsPlanned(entry.Mac);
        Plan.PlanDevice(entry.Mac);

        Log.AddNotice(
            wasAlreadyPlanned
                ? $"{entry.Mac} is already in the plan - selected its row."
                : $"{entry.Mac} added to the plan.",
            wasAlreadyPlanned
                ? null
                : "Type its address and mask into the grid. It will not be served until you do.");
    }

    /// <summary>
    /// Moves a device from the scan results into the plan.
    ///
    /// <para><b>The address is filled in, and this is not the tool guessing.</b> A scanned device
    /// told us the address it is holding; that is a fact read off the wire, not the "lowest free
    /// address" suggestion the log-row path deliberately refuses to make. It also makes the common
    /// job one gesture: a device that came up on somebody else's DHCP server is scanned, added, and
    /// Set static writes the address it already has into its flash.</para>
    ///
    /// <para>The mask is only filled when the device's address is on the selected adapter's own
    /// subnet, because that is the only case where the adapter's mask is evidence rather than a
    /// guess. Anything else is left for the user, and the row says so in amber until it is typed.</para>
    /// </summary>
    public void PlanFromScan(ScanResultViewModel result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (!result.CanBePlanned)
        {
            // The plan is keyed on MAC and this row has none. Core already worded why, and which
            // of the three reasons it is decides what the user should do next.
            Log.AddNotice(
                $"{result.AddressText} cannot go into the plan yet.",
                result.Device.PlanningObstacle);
            return;
        }

        bool wasAlreadyPlanned = Plan.IsPlanned(result.Mac);
        DeviceRowViewModel row = Plan.PlanDevice(result.Mac);

        if (!wasAlreadyPlanned)
        {
            // Mask first, then the address: each assignment commits the row, and a row carrying an
            // address with no mask is briefly amber. Harmless, but there is no reason to write it.
            string? mask = MaskForScannedAddress(result.Device.Address);
            if (mask is not null)
            {
                row.MaskText = mask;
            }

            row.IpText = result.AddressText;
            row.Notes = $"Found by scan: {result.ProductText} rev {result.RevisionText} serial {result.SerialText}";

            Log.AddNotice(
                $"{result.Mac} added to the plan at {result.AddressText} - the address it is already holding.",
                mask is null
                    ? "Type its subnet mask; it is not on this adapter's subnet, so the tool will not assume one."
                    : $"The mask {mask} came from the selected adapter. Check it against the drawing.");
        }
        else
        {
            Log.AddNotice($"{result.Mac} is already in the plan - selected its row.");
        }

        Scan.ApplyPlan(Plan.IsPlanned);
        RecompareScan();
    }

    /// <summary>
    /// Rebuilds the policy from the project's plan. Rows that cannot become a valid assignment are
    /// reported rather than skipped silently - "row 14: 255.255.0.255 is not a valid subnet mask"
    /// at the desk beats a device that never comes up on site.
    /// </summary>
    /// <param name="announceProblems">
    /// False when the change came from the grid. Every cell the user leaves rebuilds the policy, and
    /// a row that is halfway through being typed is a problem for as long as it takes to type the
    /// rest of it - a log line per keystroke would bury the requests the log exists to show. The
    /// grid shows those problems on the row itself, which is where the fix is anyway.
    /// </param>
    public void ReloadPlan(bool announceProblems = true)
    {
        IReadOnlyList<DeviceAssignment> assignments =
            _project.Devices.PlannedAssignments(out IReadOnlyList<string> problems);

        _policy.Clear();
        foreach (DeviceAssignment assignment in assignments)
        {
            _policy.Add(assignment);
        }

        // Every row, including the ones that did not become an assignment. The policy decides what
        // may be served; this decides what the log calls a device and whether it is a stranger, and
        // a MAC typed in at 7am with the address still to come is neither servable nor a stranger.
        _planIndex.Reload(_project.Devices.All());

        PlannedDeviceCount = assignments.Count;

        if (!announceProblems)
        {
            return;
        }

        foreach (string problem in problems)
        {
            Log.AddNotice($"Plan: {problem}", "Fix the row before arming Serve mode; it will never be answered.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        InterfaceBar.PropertyChanged -= OnInterfaceBarPropertyChanged;
        Plan.PlanChanged -= OnPlanChanged;
        Plan.CommissionProgress -= OnCommissionProgress;
        _server.RequestReceived -= OnRequestReceived;
        _server.ReplySent -= OnReplySent;
        _server.Fault -= OnFault;
        _server.RecordingFailed -= OnRecordingFailed;
        _server.StateChanged -= OnServerStateChanged;

        _server.Dispose();
        Tftp.Dispose();
        _project.Dispose();
    }

    [RelayCommand]
    private async Task RefreshEnvironmentAsync(CancellationToken cancellationToken)
    {
        try
        {
            PreflightResult result = await _preflight.InspectAsync(_listenPort, cancellationToken)
                .ConfigureAwait(true);

            InterfaceBar.ApplyPreflight(result);
        }
        catch (OperationCanceledException)
        {
            // The window is closing. Nothing to report.
        }
    }

    [RelayCommand(CanExecute = nameof(CanStartWatch))]
    private Task StartWatchAsync() => StartAsync(DhcpServerMode.Watch);

    [RelayCommand(CanExecute = nameof(CanStartServe))]
    private Task StartServeAsync() => StartAsync(DhcpServerMode.Serve);

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        await _server.StopAsync().ConfigureAwait(true);
        IsServeArmed = false;
        Log.Mode = DhcpServerMode.Watch;
        Log.AddNotice("Listener stopped.");
    }

    [RelayCommand]
    private void ClearLog() => Log.Clear();

    [RelayCommand]
    private void ClearScan()
    {
        _lastScan = null;
        Scan.Clear();
    }

    /// <summary>
    /// Re-runs the comparison against the plan as it is now. Called after the plan changes, so a
    /// finding about an address somebody has just fixed stops being shown as a live conflict.
    ///
    /// <para>It does not re-scan and never transmits: the devices are the ones the last scan found,
    /// and how long ago that was is exactly as true as it was a moment ago.</para>
    /// </summary>
    private void RecompareScan()
    {
        if (_lastScan is null)
        {
            return;
        }

        Scan.ApplyConformance(CompareAgainstPlan(_lastScan));
    }

    /// <summary>
    /// Asks every EtherNet/IP device on the selected adapter to identify itself.
    ///
    /// <para><b>This transmits</b>, which is why it is a button and never a timer, and why it
    /// writes an <see cref="EventCategory.Scan"/> row whether or not it finds anything: it put
    /// packets on what might be a plant network, so it belongs in the record. It sends ListIdentity
    /// and nothing else - no connection is opened to any device and nothing is written to one.</para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        if (_discovery is null || InterfaceBar.SelectedAdapter?.Nic is not { } nic)
        {
            SetError(
                "No adapter is selected.",
                "A scan goes out of exactly one adapter, and it is never chosen for you.");
            return;
        }

        ClearError();
        SetScanning(true);
        Scan.Status = $"Scanning from [{nic.Index}] {nic.Name}...";
        Log.AddNotice(
            $"Scan started on [{nic.Index}] {nic.Name}.",
            "One ListIdentity broadcast. Nothing is written to any device.");

        try
        {
            DiscoveryResult result =
                await _discovery.ScanAsync(nic, cancellationToken: cancellationToken).ConfigureAwait(true);

            _lastScan = result;

            Scan.Apply(result);
            Scan.ApplyPlan(Plan.IsPlanned);
            RecordScan(result);
            Log.AddNotice(result.Summary);

            foreach (IPAddress contested in result.ContestedAddresses)
            {
                Log.AddNotice(
                    $"More than one device answered on {contested}.",
                    "Two devices on one address is a fault on the segment, not in the scan. "
                        + "Unplug one and scan again to find out which is which.");
            }

            // What the scan has to say about the plan. This is the sentence neither list can
            // produce alone, and the reason the scan was worth building: "the address you planned
            // for the conveyor drive is one the HMI is already sitting on".
            PlanConformanceReport conformance = CompareAgainstPlan(result);
            Scan.ApplyConformance(conformance);
            RecordConformance(conformance);

            if (conformance.PlannedWithAddress > 0)
            {
                Log.AddNotice(conformance.Summary);
            }

            foreach (PlanFinding finding in conformance.Findings)
            {
                // Contested addresses are already above, worded for the segment rather than for
                // the plan. Saying it twice in the same log would train people to skim it.
                if (finding.Severity != EventSeverity.Info && finding.Kind != PlanFindingKind.ContestedAddress)
                {
                    Log.AddNotice(finding.Message);
                }
            }
        }
        catch (EnipException ex)
        {
            SetError(ex.Message, ex.Remediation);
            Scan.Status = ex.Message;
            Log.AddNotice(ex.Message, ex.Remediation);
        }
        catch (OperationCanceledException)
        {
            // The window is closing mid-scan. Nothing was written to anything, so there is nothing
            // to say that the absence of results does not already say.
            Scan.Status = "Scan cancelled.";
        }
        finally
        {
            SetScanning(false);
        }
    }

    private bool CanScan() =>
        _discovery is not null
        && !Scan.IsScanning
        && InterfaceBar.SelectedAdapter?.Nic is { CanServe: true };

    private void SetScanning(bool scanning)
    {
        Scan.IsScanning = scanning;
        ScanCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Reads the plan and compares it against what answered. Never throws for the same reason the
    /// recorders do not: the scan has already happened, and a project file that has gone read-only
    /// must not turn a completed scan into an error.
    /// </summary>
    private PlanConformanceReport CompareAgainstPlan(DiscoveryResult result)
    {
        try
        {
            return PlanConformance.Compare(_project.Devices.All(), result);
        }
        catch (PersistenceException ex)
        {
            Log.AddNotice(
                $"The scan finished, but the plan could not be read to compare it against: {ex.Message}",
                ex.Remediation);

            return PlanConformanceReport.None;
        }
    }

    /// <summary>
    /// The comparison in the append-only record: one line for the tally, and one line for each
    /// finding that needs a person.
    ///
    /// <para>Each conflict gets its own row, attributed to the plan row it is about. "1 planned
    /// address is held by a different device" is not something anybody can act on six months later;
    /// "192.168.1.51 is planned for conveyor drive (00:1D:9C:...), but PanelView 800 (00:0F:73:...)
    /// is already answering there" is.</para>
    /// </summary>
    private void RecordConformance(PlanConformanceReport report)
    {
        // Nothing in the plan had an address, so there was nothing to compare and the only possible
        // findings are "this device is not in the plan" - which the scan's own row already covers.
        // A second row saying so on every scan of a fresh project is noise in a record whose value
        // is that everything in it is worth reading.
        if (report.PlannedWithAddress == 0)
        {
            return;
        }

        try
        {
            _project.Events.Append(
                report.Severity,
                EventCategory.Scan,
                report.Summary,
                detail: new EventDetail()
                    .Add("plannedWithAddress", report.PlannedWithAddress)
                    .Add("confirmed", report.ConfirmedCount)
                    .Add("conflicts", report.Conflicts.Count));

            foreach (PlanFinding finding in report.Findings)
            {
                if (finding.Severity == EventSeverity.Info)
                {
                    continue;
                }

                _project.Events.Append(
                    finding.Severity,
                    EventCategory.Scan,
                    finding.Message,
                    target: finding.Address?.ToString(),
                    deviceId: finding.DeviceId > 0 ? finding.DeviceId : null,
                    detail: new EventDetail().Add("finding", finding.Kind));
            }
        }
        catch (PersistenceException ex)
        {
            RecordingIsIncomplete = true;
            Log.AddNotice($"Could not record what the scan said about the plan: {ex.Message}", ex.Remediation);
        }
    }

    /// <summary>
    /// The scan's own line in the append-only record. Never throws: a project file that has gone
    /// read-only must not turn a completed scan into an error.
    /// </summary>
    private void RecordScan(DiscoveryResult result)
    {
        try
        {
            _project.Events.Append(
                result.ContestedAddresses.Count > 0 ? EventSeverity.Warn : EventSeverity.Info,
                EventCategory.Scan,
                result.Summary,
                target: $"[{result.Report.Nic.Index}] {result.Report.Nic.Name}",
                detail: new EventDetail()
                    .Add("probes", result.Report.ProbesSent)
                    .Add("answered", result.Devices.Count)
                    .Add("refused", result.Report.Refused)
                    .Add("ignored", result.Report.Ignored)
                    .Add("unresolved", result.Unresolved.Count));
        }
        catch (PersistenceException ex)
        {
            RecordingIsIncomplete = true;
            Log.AddNotice($"Could not record the scan: {ex.Message}", ex.Remediation);
        }
    }

    /// <summary>
    /// What an import is checked against. With an adapter selected it also refuses a plan holding
    /// this machine's own address or one off the adapter's segment; with none selected those three
    /// checks do not run, which is how a plan for a subnet nobody is on yet still imports.
    /// </summary>
    private PlanImportContext ImportContext() =>
        InterfaceBar.SelectedAdapter?.Nic is { } nic
            ? PlanImportContext.ForAdapter(nic, _oui)
            : new PlanImportContext { Oui = _oui };

    /// <summary>
    /// An import or export in the append-only record, including a refusal - what a plan file was
    /// asked to do and what came of it is part of the commissioning record. Never throws, for the
    /// same reason <see cref="RecordScan"/> does not.
    /// </summary>
    private void RecordPlanFile(EventSeverity severity, string message, string path, int imported)
    {
        try
        {
            _project.Events.Append(
                severity,
                EventCategory.App,
                message,
                target: path,
                detail: new EventDetail().Add("devices", imported));
        }
        catch (PersistenceException ex)
        {
            RecordingIsIncomplete = true;
            Log.AddNotice($"Could not record that in the project: {ex.Message}", ex.Remediation);
        }
    }

    private bool CanStartWatch() => RunState is ServerRunState.Stopped or ServerRunState.Faulted;

    private bool CanStartServe() =>
        CanStartWatch()
        && IsServeArmed
        && InterfaceBar.CanArmServeMode;

    private bool CanStop() => RunState is ServerRunState.Listening;

    private async Task StartAsync(DhcpServerMode mode)
    {
        ClearError();

        AdapterOption? adapter = InterfaceBar.SelectedAdapter;

        if (mode == DhcpServerMode.Serve && adapter is null)
        {
            SetError(
                "No adapter is selected.",
                "Serve mode answers on exactly one adapter, and it is never chosen for you.");
            return;
        }

        var options = new DhcpServerOptions
        {
            ListenPort = _listenPort,
            Mode = mode,

            // Serve mode is pinned to the chosen adapter: the tool must never answer a device on
            // a port nobody selected. Watch mode listens everywhere on purpose - a request landing
            // on the adapter you did not pick is precisely the thing worth being told about, and
            // filtering it out here would hide the answer to "why is nothing happening".
            InterfaceIndexFilter = mode == DhcpServerMode.Serve ? adapter?.Index : null,

            SendMode = UsePerSocketBind
                ? ReplySendMode.PerSocketBind
                : ReplySendMode.UnicastInterfaceOption,
        };

        Log.Mode = mode;
        Log.SelectedInterfaceIndex = adapter?.Index;

        try
        {
            await _server.StartAsync(options, _project).ConfigureAwait(true);

            Log.AddNotice(mode == DhcpServerMode.Serve
                ? $"Serving {PlannedDeviceCount} planned device(s) on {adapter?.DisplayName}."
                : "Watching. Nothing will be transmitted.");
        }
        catch (DhcpBindException ex)
        {
            SetError(ex.Message, ex.Remediation);
            Log.AddNotice(ex.Message, ex.Remediation);
        }
        catch (InvalidOperationException ex)
        {
            SetError(ex.Message, null);
        }
    }

    private void OnRequestReceived(object? sender, DhcpRequestEventArgs e) => RunOnUi(() =>
    {
        Log.OnRequest(e);
        Plan.NoteSeen(e.Mac);
        Tftp.NoteDhcpRequest(e);
        InterfaceBar.NoteArrival(e.ArrivalInterfaceIndex, e.ArrivalNic?.Name ?? "unresolved adapter");
    });

    private void OnReplySent(object? sender, DhcpReplyEventArgs e) => RunOnUi(() =>
    {
        Log.OnReply(e);

        // Only a reply that carried an assignment. A refusal reached the wire too, but the device
        // did not get an address, and a plan row claiming otherwise would be a lie in the grid.
        if (e.Assignment is not null)
        {
            Plan.NoteServed(e.Mac);
        }

        Tftp.NoteDhcpReply(e);
    });

    private void OnFault(object? sender, DhcpFaultEventArgs e) => RunOnUi(() =>
    {
        Log.OnFault(e);
        if (e.IsFatal)
        {
            SetError(e.Message, e.Remediation);
        }
    });

    private void OnRecordingFailed(object? sender, PersistenceFailedEventArgs e) => RunOnUi(() =>
    {
        RecordingIsIncomplete = true;
        Log.AddNotice($"Could not record the {e.What}: {e.Message}");
    });

    private void OnServerStateChanged(object? sender, EventArgs e) => RunOnUi(() =>
    {
        RunState = _server.State;
        InterfaceBar.ApplyListener(RunState, _server.ActiveOptions?.Mode ?? DhcpServerMode.Watch, _server.StopReason);
        Tftp.NoteDhcpState(RunState, _server.ActiveOptions?.Mode ?? DhcpServerMode.Watch);

        if (RunState is ServerRunState.Faulted && _server.StopReason is { } reason)
        {
            SetError(reason, "Start it again. If it recurs, check whether the adapter was removed or reset.");
        }
    });

    private void OnInterfaceBarPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(InterfaceBarViewModel.SelectedAdapter))
        {
            Log.SelectedInterfaceIndex = InterfaceBar.SelectedAdapter?.Index;

            // The TFTP watch refuses only on the selected adapter, so it cannot start without one.
            Tftp.NoteAdapterChanged();
        }

        if (e.PropertyName is nameof(InterfaceBarViewModel.SelectedAdapter)
            or nameof(InterfaceBarViewModel.CanArmServeMode))
        {
            StartServeCommand.NotifyCanExecuteChanged();
        }

        if (e.PropertyName is nameof(InterfaceBarViewModel.SelectedAdapter))
        {
            // A scan needs an adapter that can source one, same as a reply does - and an adapter
            // that has just been unplugged must not leave the button live.
            ScanCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// The mask to give a scanned device, or null when there is no honest answer.
    ///
    /// <para>Only the selected adapter's mask, and only when the device is on that adapter's
    /// subnet. That is the one case where the mask is evidence: the datagram arrived here, on this
    /// segment, addressed within it. A device answering from elsewhere may be behind a router with
    /// a completely different prefix, and a mask invented for it would be wrong in the way that is
    /// hardest to see - the device would come up, mostly work, and fail to reach half the plant.</para>
    /// </summary>
    private string? MaskForScannedAddress(IPAddress address)
    {
        if (InterfaceBar.SelectedAdapter?.Nic is not { Mask: { } mask } nic)
        {
            return null;
        }

        return Ipv4Subnet.TryCreate(nic.IPv4, mask, out Ipv4Subnet subnet) && subnet.Contains(address)
            ? mask.ToString()
            : null;
    }

    private void OnPlanChanged(object? sender, EventArgs e)
    {
        ReloadPlan(announceProblems: false);

        // The scan list is showing what the plan said a moment ago. It has just changed.
        Scan.ApplyPlan(Plan.IsPlanned);
        RecompareScan();
    }

    private void OnCommissionProgress(object? sender, CommissionProgressEventArgs e) =>
        RunOnUi(() => Log.AddNotice(e.Message));

    /// <summary>
    /// Swapping the file a running server records into would tear a commissioning record in half,
    /// so every operation that changes which project is open asks first.
    /// </summary>
    private bool RequireStopped(string what)
    {
        if (Tftp.IsRunning)
        {
            SetError(
                "The TFTP watch is still running.",
                $"Stop it on the TFTP tab before {what}, so the event log stays in one piece.");
            return false;
        }

        if (RunState is ServerRunState.Stopped or ServerRunState.Faulted)
        {
            return true;
        }

        SetError("The listener is still running.", $"Stop it before {what}, so the event log stays in one piece.");
        return false;
    }

    /// <summary>Makes <paramref name="opened"/> the project everything points at.</summary>
    private void Swap(ProjectStore opened)
    {
        ProjectStore previous = _project;
        _project = opened;

        if (!ReferenceEquals(previous, opened))
        {
            previous.Dispose();
        }

        RecordingIsIncomplete = false;
        ClearError();
        ApplyProject(opened);
    }

    private void ApplyProject(ProjectStore project)
    {
        ProjectName = project.Project.Name;
        ProjectPath = project.IsInMemory ? null : project.FilePath;
        IsUnsavedLocation = project.IsInMemory;

        RecordBuild(project);

        Plan.Load(project);
        ReloadPlan();
    }

    /// <summary>
    /// Puts the build that is about to write to this file at the top of its record.
    ///
    /// <para>A project file is an account of what was done to somebody's plant equipment, and an
    /// account that cannot say which version of the tool produced it is missing the fact you need
    /// once a bug has been found and fixed. It goes in as an ordinary event rather than a schema
    /// column so that a file written by a later build still reads correctly in this one.</para>
    ///
    /// <para>Never throws, same rule as every other recorder here.</para>
    /// </summary>
    private void RecordBuild(ProjectStore project)
    {
        if (_buildStamp is not { } stamp)
        {
            return;
        }

        try
        {
            project.Events.Append(EventSeverity.Info, EventCategory.App, stamp);
        }
        catch (PersistenceException ex)
        {
            RecordingIsIncomplete = true;
            Log.AddNotice($"Could not record which build opened this project: {ex.Message}", ex.Remediation);
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
