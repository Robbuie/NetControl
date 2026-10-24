// UseWPF drops System.IO from the implicit usings, because WPF ships System.Windows.Shapes.Path
// and the SDK will not let two Path types collide. Save As needs File, so this file asks for it
// back explicitly rather than the whole project doing so.
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetControl.App.Composition;
using NetControl.App.Diagnostics;
using NetControl.App.Serving;
using NetControl.Core;
using NetControl.Core.Capture;
using NetControl.Core.Commissioning;
using NetControl.Core.DeviceHealth;
using NetControl.Core.Dhcp;
using NetControl.Core.Discovery;
using NetControl.Core.Enip;
using NetControl.Core.Interfaces;
using NetControl.Core.Oui;
using NetControl.Core.Persistence;
using NetControl.Core.Reachability;
using NetControl.Core.Reporting;
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
    /// Ping-and-ARP over the selected adapter's whole subnet. Null in a build without one, in which
    /// case Scan is the EtherNet/IP identity request alone and the checkbox is not offered.
    /// </summary>
    private readonly SubnetSweep? _sweep;

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
        Action<TftpSettings>? saveTftpSettings = null,
        IPinger? pinger = null,
        DeviceHealthReader? healthReader = null,
        ServiceProbe? serviceProbe = null,
        ICaptureProvider? capture = null,
        SubnetSweep? sweep = null)
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
        _sweep = sweep;
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

        // The Diagnostics tab. It reads the project through a lambda for the same reason the TFTP tab
        // does - File, Open swaps it - and holds the grid so Ping plan can fill in the Reach column.
        Diagnostics = new DiagnosticsViewModel(
            dispatcher,
            () => _project,
            Plan,
            pinger,
            healthReader: healthReader,
            serviceProbe: serviceProbe,
            selectedAdapter: () => InterfaceBar.SelectedAdapter?.Nic);

        Modbus = new ModbusViewModel(() => _project);

        // Both raw-Ethernet tabs share one provider. Nothing in it is touched until a tab asks - a
        // machine without Npcap never so much as looks for the driver.
        ICaptureProvider frames = capture ?? new NpcapProvider();
        Passive = new PassiveViewModel(dispatcher, () => _project, () => InterfaceBar.SelectedAdapter?.Nic, frames, oui);
        Profinet = new ProfinetViewModel(() => _project, () => InterfaceBar.SelectedAdapter?.Nic, frames);

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
        UpdateSweepPlan();
    }

    public InterfaceBarViewModel InterfaceBar { get; }

    public RequestLogViewModel Log { get; }

    /// <summary>The plan, editable. Writes straight through to the project file as cells are left.</summary>
    public DeviceGridViewModel Plan { get; }

    /// <summary>What the last scan found. Empty, and harmless, until somebody presses Scan.</summary>
    public ScanResultsViewModel Scan { get; } = new();

    /// <summary>The TFTP tab: the UDP/69 watch, and which step of an image backup failed.</summary>
    public TftpViewModel Tftp { get; }

    /// <summary>One device's reachability, health and services, and pinging the whole plan.</summary>
    public DiagnosticsViewModel Diagnostics { get; }

    /// <summary>The Modbus tab. Reads only.</summary>
    public ModbusViewModel Modbus { get; }

    /// <summary>The Passive tab: listen-only inventory. Needs Npcap.</summary>
    public PassiveViewModel Passive { get; }

    /// <summary>The PROFINET tab: DCP identify, name and address. Needs Npcap.</summary>
    public ProfinetViewModel Profinet { get; }

    /// <summary>Raised when something has been pointed at the Modbus tab, so the window can show it.</summary>
    public event EventHandler? ModbusRequested;

    /// <summary>
    /// Raised when something has been pointed at the Diagnostics tab, so the window can bring it to
    /// the front. A view concern - which tab is showing - so the view model only says that it
    /// happened.
    /// </summary>
    public event EventHandler? DiagnosticsRequested;

    /// <summary>
    /// What the subnet calculator starts with: the selected adapter's own address and prefix, which is
    /// the subnet the question is nearly always about. Empty with no adapter selected.
    /// </summary>
    public string SubnetCalculatorStart =>
        InterfaceBar.SelectedAdapter?.Nic is { IPv4: { } address, PrefixLength: { } prefix }
            ? $"{address}/{prefix}"
            : string.Empty;

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
    /// Writes the commissioning record out as one self-contained HTML page - the startup record for
    /// the customer. See <see cref="CommissioningReport"/>: it reads the project file and nothing else,
    /// so it says the same thing whenever and wherever it is generated.
    ///
    /// <para>Exporting is itself recorded, so a record handed over can be matched to the moment it was
    /// taken.</para>
    /// </summary>
    /// <returns>True when the file was written.</returns>
    public bool ExportReport(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            string html = CommissioningReport.BuildHtml(
                _project,
                _time.GetUtcNow(),
                $"NetControl {NetControl.App.Diagnostics.BuildInfo.Version}");

            File.WriteAllText(path, html, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            ClearError();
            Log.AddNotice($"Commissioning report written to {path}.");
            RecordPlanFile(EventSeverity.Info, "Commissioning report exported.", path, _project.Devices.Count);
            return true;
        }
        catch (PersistenceException ex)
        {
            SetError($"Could not read the project to report on it: {ex.Message}", ex.Remediation);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetError(
                $"Could not write '{path}': {ex.Message}",
                "Close the file if a browser has it open, or save it under a different name.");
        }

        return false;
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
                result.PlanningObstacle);
            return;
        }

        bool wasAlreadyPlanned = Plan.IsPlanned(result.Mac);
        DeviceRowViewModel row = Plan.PlanDevice(result.Mac);

        if (!wasAlreadyPlanned)
        {
            // Mask first, then the address: each assignment commits the row, and a row carrying an
            // address with no mask is briefly amber. Harmless, but there is no reason to write it.
            string? mask = MaskForScannedAddress(result.Address);
            if (mask is not null)
            {
                row.MaskText = mask;
            }

            row.IpText = result.AddressText;
            row.Notes = result.IsEtherNetIp
                ? $"Found by scan: {result.ProductText} rev {result.RevisionText} serial {result.SerialText}"
                : $"Found by subnet sweep ({result.ResponseText})"
                    + (result.VendorText.Length > 0 ? $": {result.VendorText}" : string.Empty);

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
    /// Points the Diagnostics tab at a plan row's address. Sends nothing: the tab's buttons do that.
    /// </summary>
    public void DiagnosePlanRow(DeviceRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (!row.TryBuild(out DeviceRecord? record) || record?.PlannedIp is not { } ip)
        {
            Log.AddNotice(
                "That plan row has no address to diagnose.",
                "Type its planned address first - diagnostics go to one address, and the tool does not guess one.");
            return;
        }

        Diagnostics.SetTarget(ip, record.PlannedMask, record.DisplayName ?? record.Mac.ToString());
        DiagnosticsRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Points the Modbus tab at a plan row's planned address. Sends nothing.</summary>
    public void ModbusPlanRow(DeviceRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (!row.TryBuild(out DeviceRecord? record) || record?.PlannedIp is not { } ip)
        {
            Log.AddNotice(
                "That plan row has no address to read from.",
                "Type its planned address first - a read goes to one address, and the tool does not guess one.");
            return;
        }

        Modbus.SetTarget(ip, record.DisplayName ?? record.Mac.ToString());
        ModbusRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Points the Diagnostics tab at a scanned device's address - the one it reported. Sends nothing.
    /// </summary>
    public void DiagnoseScanResult(ScanResultViewModel result)
    {
        ArgumentNullException.ThrowIfNull(result);

        Diagnostics.SetTarget(
            result.Address,
            mask: null,
            name: result.ProductText.Length > 0 ? result.ProductText : result.VendorText.Length > 0 ? result.VendorText : null);
        DiagnosticsRequested?.Invoke(this, EventArgs.Empty);
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
    /// Finds what is on the network, in up to three passes, all from the selected adapter:
    ///
    /// <list type="number">
    /// <item>For <b>each address the adapter holds</b>, an EtherNet/IP identity broadcast sourced from
    /// that address - a device only answers to its own subnet - and, when the box is ticked, a sweep
    /// of that address's subnet. A laptop given a second address to reach a second subnet on the same
    /// panel switch gets both subnets, each scanned as completely as the first.</item>
    /// <item>For <b>each routed range</b> typed beside the button, a ping sweep of that range and then
    /// a unicast identity request to each address that answered - a broadcast stops at the router,
    /// and so does ARP, so those rows carry no MAC.</item>
    /// </list>
    ///
    /// <para><b>This transmits</b>, which is why it is a button and never a timer, and why every pass
    /// writes its own <see cref="EventCategory.Scan"/> row whether or not it found anything. It sends
    /// ListIdentity and ICMP echo; no connection is opened to any device and nothing is written to
    /// one.</para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        if (InterfaceBar.SelectedAdapter?.Nic is not { } nic)
        {
            SetError(
                "No adapter is selected.",
                "A scan goes out of exactly one adapter, and it is never chosen for you.");
            return;
        }

        // The routed ranges are checked before anything is sent: one bad range refuses the whole
        // scan with nothing on the wire, the same rule a bad address in a ping of the plan follows.
        if (Scan.RoutedRangesProblem is { } rangeProblem)
        {
            SetError("The routed ranges could not be read, so nothing was sent.", rangeProblem);
            Scan.Status = "Nothing was sent: " + rangeProblem;
            return;
        }

        IReadOnlyList<SweepRange> routed = _sweep is null ? [] : Scan.ParsedRoutedRanges;
        SaveRoutedRanges();

        bool sweep = _sweep is not null && Scan.SweepSubnet && Scan.CanSweep;
        IReadOnlyList<NicInfo> parts = [.. nic.PerAddress().Where(n => n.CanServe)];

        ClearError();
        SetScanning(true);
        Scan.Begin($"Scanning from [{nic.Index}] {nic.Name}...");
        Log.AddNotice(
            $"Scan started on [{nic.Index}] {nic.Name}"
                + (parts.Count > 1 ? $", from each of its {parts.Count} addresses." : "."),
            (_discovery is null ? string.Empty : "One ListIdentity broadcast per address. ")
                + (sweep ? "One echo request to each address on the subnet. " : string.Empty)
                + (routed.Count > 0 ? $"{routed.Count} routed range(s): echo requests, then ListIdentity to whatever answered. " : string.Empty)
                + "Nothing is written to any device.");

        var summaries = new List<string>();
        var identified = new List<DiscoveryResult>();

        try
        {
            foreach (NicInfo part in parts)
            {
                if (_discovery is not null)
                {
                    DiscoveryResult? result = await IdentifyAsync(part, cancellationToken).ConfigureAwait(true);

                    if (result is null)
                    {
                        return;
                    }

                    identified.Add(result);
                    summaries.Add(result.Summary);
                    Scan.Merge(result);
                }

                if (sweep && _sweep!.TryPlan(part, out _, out _, out _))
                {
                    SubnetSweepResult? swept = await SweepAsync(
                        () => _sweep.SweepAsync(part, cancellationToken), Lead(summaries)).ConfigureAwait(true);

                    if (swept is not null)
                    {
                        summaries.Add(swept.Summary);
                    }
                }

                Scan.Status = Lead(summaries).TrimEnd();
            }

            foreach (SweepRange range in routed)
            {
                // A typed range that sits on one of this adapter's own subnets has just been swept
                // in full, ARP and all; sweeping it again would only send the same pings twice.
                if (sweep && parts.Any(p => Ipv4Subnet.TryCreate(p.IPv4, p.Mask, out Ipv4Subnet s) && range.IsWithin(s)))
                {
                    continue;
                }

                SubnetSweepResult? swept = await SweepAsync(
                    () => _sweep!.SweepRangeAsync(nic, range, cancellationToken), Lead(summaries)).ConfigureAwait(true);

                if (swept is null)
                {
                    continue;
                }

                summaries.Add(swept.Summary);

                // What answered ping gets asked what it is, by name. A broadcast would stop at the
                // router; one datagram to each address that is known to be there does not.
                List<IPAddress> answered = [.. swept.Hosts.Where(h => h.AnsweredPing).Select(h => h.Address)];

                if (_discovery is not null && answered.Count > 0)
                {
                    DiscoveryResult? result = await IdentifyRoutedAsync(nic, answered, cancellationToken).ConfigureAwait(true);

                    if (result is not null)
                    {
                        identified.Add(result);
                        summaries.Add(result.Summary);
                        Scan.Merge(result);
                    }
                }

                Scan.Status = Lead(summaries).TrimEnd();
            }

            // What the scan has to say about the plan, over everything that identified itself. This
            // is the sentence neither list can produce alone, and the reason the scan was worth
            // building: "the address you planned for the conveyor drive is one the HMI is already
            // sitting on".
            if (identified.Count > 0)
            {
                DiscoveryResult all = Combine(identified);
                _lastScan = all;
                CompareAndReport(all);
            }
            else
            {
                _lastScan = null;
            }

            Scan.ApplyPlan(Plan.IsPlanned);
            Scan.Status = Lead(summaries).TrimEnd();
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

    private static string Lead(List<string> summaries) =>
        summaries.Count == 0 ? string.Empty : string.Join(" ", summaries) + " ";

    /// <summary>Several identity passes as one result, for the comparison against the plan.</summary>
    private static DiscoveryResult Combine(List<DiscoveryResult> results) =>
        results.Count == 1 ? results[0] : new DiscoveryResult(results[0].Report, [.. results.SelectMany(r => r.Devices)]);

    /// <summary>
    /// The EtherNet/IP half of a scan from one of the adapter's addresses. Returns the result, or
    /// null when it could not run - in which case the reason is already on screen and in the log.
    /// </summary>
    private async Task<DiscoveryResult?> IdentifyAsync(NicInfo nic, CancellationToken cancellationToken)
    {
        try
        {
            DiscoveryResult result =
                await _discovery!.ScanAsync(nic, DirectlyAsked(), cancellationToken).ConfigureAwait(true);

            RecordScan(result);
            Log.AddNotice(result.Summary);

            // What changed since the last scan of this subnet, and then this scan into the history
            // for the next one to be compared with. After the scan's own row, so the record reads
            // in the order it happened. Per address, because the history is kept per subnet.
            CompareWithLastScan(result);

            foreach (IPAddress contested in result.ContestedAddresses)
            {
                Log.AddNotice(
                    $"More than one device answered on {contested}.",
                    "Two devices on one address is a fault on the segment, not in the scan. "
                        + "Unplug one and scan again to find out which is which.");
            }

            return result;
        }
        catch (EnipException ex)
        {
            SetError(ex.Message, ex.Remediation);
            Scan.Status = ex.Message;
            Log.AddNotice(ex.Message, ex.Remediation);
            return null;
        }
    }

    /// <summary>
    /// Asks each routed address that answered ping what it is. Not kept in the scan history: that is
    /// compared per subnet of this adapter, and a routed range is not one. A failure here is reported
    /// and the scan carries on with the rest - the sweep it follows has already found the hosts.
    /// </summary>
    private async Task<DiscoveryResult?> IdentifyRoutedAsync(
        NicInfo nic, List<IPAddress> targets, CancellationToken cancellationToken)
    {
        try
        {
            DiscoveryResult result = await _discovery!.IdentifyAsync(nic, targets, cancellationToken).ConfigureAwait(true);

            RecordScan(result);
            Log.AddNotice(result.Summary);
            return result;
        }
        catch (EnipException ex)
        {
            Log.AddNotice(ex.Message, ex.Remediation);
            return null;
        }
    }

    /// <summary>The plan comparison over everything the scan identified: on screen, in the log, in the record.</summary>
    private void CompareAndReport(DiscoveryResult result)
    {
        PlanConformanceReport conformance = CompareAgainstPlan(result);
        Scan.ApplyConformance(conformance);
        RecordConformance(conformance);

        if (conformance.PlannedWithAddress > 0)
        {
            Log.AddNotice(conformance.Summary);
        }

        foreach (PlanFinding finding in conformance.Findings)
        {
            // Contested addresses are already in the log, worded for the segment rather than for the
            // plan. Saying it twice in the same log would train people to skim it.
            if (finding.Severity != EventSeverity.Info && finding.Kind != PlanFindingKind.ContestedAddress)
            {
                Log.AddNotice(finding.Message);
            }
        }
    }

    /// <summary>
    /// One ping sweep - a subnet of this adapter's or a routed range - joined onto what is on screen.
    /// A sweep that is refused is reported and the scan carries on: the other passes still ran.
    /// </summary>
    private async Task<SubnetSweepResult?> SweepAsync(Func<Task<SubnetSweepResult>> run, string lead)
    {
        // Progress arrives from whichever thread a ping finished on, and may still be queued for the
        // UI thread after the result is on screen. The flag keeps a late count from overwriting it.
        bool finished = false;

        void OnProgress(object? sender, PingProgressEventArgs e) => RunOnUi(() =>
        {
            if (!finished)
            {
                Scan.Status = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{lead}Sweeping: {e.Completed} of {e.Total} addresses...");
            }
        });

        _sweep!.Progress += OnProgress;

        try
        {
            SubnetSweepResult result = await run().ConfigureAwait(true);

            finished = true;

            Scan.ApplySweep(result);
            RecordSweep(result);
            Log.AddNotice(result.Summary);
            return result;
        }
        catch (ReachabilityException ex)
        {
            finished = true;
            Log.AddNotice($"Not swept: {ex.Message}", ex.Remediation);
            return null;
        }
        finally
        {
            finished = true;
            _sweep.Progress -= OnProgress;
        }
    }

    private bool CanScan() =>
        (_discovery is not null || _sweep is not null)
        && !Scan.IsScanning
        && InterfaceBar.SelectedAdapter?.Nic is { CanServe: true };

    /// <summary>
    /// Re-reads what a sweep from the selected adapter would cover, for the checkbox's caption: every
    /// subnet the adapter has an address on. Sends nothing; called whenever the adapter changes.
    /// </summary>
    private void UpdateSweepPlan()
    {
        Scan.CanRoute = _sweep is not null;

        if (_sweep is null)
        {
            Scan.CanSweep = false;
            Scan.SweepCaption = "Subnet sweep is not available in this build";
            Scan.SweepTooltip = null;
            return;
        }

        var subnets = new List<Ipv4Subnet>();
        int total = 0;
        string? problem = null;

        if (InterfaceBar.SelectedAdapter?.Nic is { } nic)
        {
            foreach (NicInfo part in nic.PerAddress())
            {
                if (_sweep.TryPlan(part, out Ipv4Subnet subnet, out int addresses, out string? why))
                {
                    subnets.Add(subnet);
                    total += addresses;
                }
                else
                {
                    problem ??= why;
                }
            }
        }
        else
        {
            _sweep.TryPlan(null, out _, out _, out problem);
        }

        if (subnets.Count > 0)
        {
            int seconds = (int)Math.Ceiling(_sweep.Options.Estimate(total).TotalSeconds);

            Scan.CanSweep = true;
            Scan.SweepCaption = string.Create(
                CultureInfo.InvariantCulture,
                $"Also ping every address on {string.Join(" and ", subnets)} ({total:N0} addresses, about {seconds} s)");
            Scan.SweepTooltip =
                "Finds everything with an IP address - PCs, switches, HMIs, cameras and devices that do not speak "
                + "EtherNet/IP. One echo request to each address, a few at a time, then a read of this PC's ARP "
                + "cache, which also catches devices set to ignore ping. Nothing is connected to and nothing is "
                + "written."
                + (subnets.Count > 1
                    ? " This adapter has more than one address, so each subnet is scanned from its own address."
                    : " To scan a second subnet on the same cable, give this adapter a second address on it "
                        + "(Advanced TCP/IP settings) - it will be listed here.")
                + (problem is null ? string.Empty : " Not swept: " + problem);
        }
        else
        {
            Scan.CanSweep = false;
            Scan.SweepCaption = "Subnet sweep unavailable on this adapter";
            Scan.SweepTooltip = problem;
        }
    }

    /// <summary>
    /// Keeps the routed ranges with the project, because they are the site's - another plant has
    /// other VLANs. Written when a scan uses them rather than on every keystroke, and the change is
    /// a row in the record like every other setting that travels with the file.
    /// </summary>
    private void SaveRoutedRanges()
    {
        string text = string.Join(", ", Scan.ParsedRoutedRanges.Select(r => r.Text));

        try
        {
            if ((_project.Settings.Get(RoutedRangesSetting) ?? string.Empty) == text)
            {
                return;
            }

            _project.Settings.Set(RoutedRangesSetting, text);
            _project.Events.Append(
                EventSeverity.Info,
                EventCategory.App,
                text.Length == 0 ? "Routed scan ranges cleared." : $"Routed scan ranges set to {text}.");
        }
        catch (PersistenceException ex)
        {
            RecordingIsIncomplete = true;
            Log.AddNotice($"Could not save the routed ranges with the project: {ex.Message}", ex.Remediation);
        }
    }

    private const string RoutedRangesSetting = "scan.routedRanges";

    /// <summary>
    /// One row for the sweep, whether or not it found anything: it put an echo request on every
    /// address of somebody's subnet. Never throws, for the same reason the other recorders do not.
    /// </summary>
    private void RecordSweep(SubnetSweepResult result)
    {
        try
        {
            _project.Events.Append(
                result.Severity,
                EventCategory.Scan,
                result.Summary,
                target: $"[{result.Nic.Index}] {result.Nic.Name}",
                detail: new EventDetail()
                    .Add("scope", result.Scope)
                    .Add("routed", result.IsRouted)
                    .Add("pinged", result.AddressesPinged)
                    .Add("found", result.Hosts.Count)
                    .Add("answeredPing", result.AnsweredPing)
                    .Add("arpOnly", result.ArpOnly)
                    .Add("arpChecked", result.ArpChecked));
        }
        catch (PersistenceException ex)
        {
            RecordingIsIncomplete = true;
            Log.AddNotice($"Could not record the subnet sweep: {ex.Message}", ex.Remediation);
        }
    }

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
    /// Compares this scan with the last one of the same subnet, shows what changed, and adds this one
    /// to the history. A scan of a different segment is not a "before", so an adapter with no
    /// subnet - or a subnet never scanned - compares with nothing and only records.
    ///
    /// <para>The comparison goes into the record only when there was something to compare with: a
    /// first scan of a subnet has nothing to say about change, and a row saying so on every first
    /// scan would be noise. Changes that need a person get a row each; the rest are in the tally.</para>
    ///
    /// <para>Never throws, for the same reason the other recorders do not.</para>
    /// </summary>
    /// <summary>
    /// Planned devices known to ignore a broadcast ListIdentity, asked at their own planned address
    /// as well. Each one is checked as one host first; an address that is not is simply left out,
    /// because the scan's broadcast still goes and a quirk must not be able to fail the whole scan.
    /// </summary>
    private List<IPAddress>? DirectlyAsked()
    {
        var targets = new List<IPAddress>();

        foreach (DeviceRowViewModel row in Plan.Rows)
        {
            if (row.Quirks.HasFlag(DeviceQuirks.IgnoresBroadcastDiscovery)
                && PlanValidation.TryParseIPv4(row.IpText?.Trim() ?? string.Empty, out IPAddress? ip)
                && ip is not null
                && UnicastTarget.TryCheck(ip, null, out _)
                && !targets.Contains(ip))
            {
                targets.Add(ip);
            }
        }

        return targets.Count == 0 ? null : targets;
    }

    private void CompareWithLastScan(DiscoveryResult result)
    {
        NicInfo nic = result.Report.Nic;
        IReadOnlyList<ScanSightingRecord> sightings = ScanSightings.From(result);

        try
        {
            InventoryDiffResult? diff = null;

            if (Ipv4Subnet.TryCreate(nic.IPv4, nic.Mask, out Ipv4Subnet subnet)
                && _project.Scans.LatestOn(subnet) is { } earlier)
            {
                diff = InventoryDiff.Compare(_project.Scans.Sightings(earlier.Id), sightings, earlier.Utc);
            }

            _project.Scans.Record(_time.GetUtcNow(), $"[{nic.Index}] {nic.Name}", nic.IPv4, nic.Mask, sightings);

            Scan.ApplyChanges(diff);

            if (diff is null)
            {
                return;
            }

            Log.AddNotice(diff.Summary);

            _project.Events.Append(
                diff.Severity,
                EventCategory.Scan,
                diff.Summary,
                target: $"[{nic.Index}] {nic.Name}",
                detail: new EventDetail()
                    .Add("moved", diff.Count(InventoryChangeKind.Moved))
                    .Add("differentDevice", diff.Count(InventoryChangeKind.DifferentDevice))
                    .Add("replaced", diff.Count(InventoryChangeKind.Replaced))
                    .Add("firmwareChanged", diff.Count(InventoryChangeKind.FirmwareChanged))
                    .Add("new", diff.Count(InventoryChangeKind.New))
                    .Add("notAnswering", diff.Count(InventoryChangeKind.NotAnswering))
                    .Add("unchanged", diff.Unchanged));

            foreach (InventoryChange change in diff.Changes.Where(c => c.Severity != EventSeverity.Info))
            {
                Log.AddNotice(change.Message);
                _project.Events.Append(
                    change.Severity,
                    EventCategory.Scan,
                    change.Message,
                    target: change.Address.ToString(),
                    detail: new EventDetail().Add("change", change.Kind));
            }
        }
        catch (PersistenceException ex)
        {
            RecordingIsIncomplete = true;
            Log.AddNotice($"Could not keep this scan in the project's scan history: {ex.Message}", ex.Remediation);
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

    /// <summary>
    /// The project's boot options, as stored. A value that no longer parses is left out and said
    /// out loud rather than served.
    /// </summary>
    public BootOptions LoadBootOptions()
    {
        try
        {
            BootOptions boot = BootOptionsSettings.Load(_project.Settings, out IReadOnlyList<string> problems);

            foreach (string problem in problems)
            {
                Log.AddNotice($"A stored DHCP boot option was not used: {problem}", "Open Listener > DHCP boot options and save it again.");
            }

            return boot;
        }
        catch (PersistenceException ex)
        {
            Log.AddNotice($"Could not read the DHCP boot options: {ex.Message}", ex.Remediation);
            return BootOptions.None;
        }
    }

    /// <summary>
    /// Stores new boot options in the project and records the change. They take effect the next
    /// time Serve starts; a running listener keeps what it started with, and the notice says so.
    /// </summary>
    public bool SaveBootOptions(BootOptions boot)
    {
        ArgumentNullException.ThrowIfNull(boot);

        BootOptions before = LoadBootOptions();

        try
        {
            BootOptionsSettings.Save(_project.Settings, boot);
            _project.Events.Append(
                EventSeverity.Info,
                EventCategory.App,
                $"DHCP boot options set to: {boot.Describe()} (were: {before.Describe()}).",
                detail: new EventDetail().Add("operation", "bootOptions"));
        }
        catch (PersistenceException ex)
        {
            SetError($"Could not save the DHCP boot options: {ex.Message}", ex.Remediation);
            return false;
        }

        Log.AddNotice(
            $"DHCP boot options: {boot.Describe()}.",
            RunState is ServerRunState.Listening
                ? "The listener that is running keeps the options it started with. Stop and start Serve to use these."
                : "Used from the next time Serve starts.");
        return true;
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

            // Read at start, not per reply: what a running listener serves must not change under it.
            Boot = mode == DhcpServerMode.Serve ? LoadBootOptions() : BootOptions.None,
        };

        Log.Mode = mode;
        Log.SelectedInterfaceIndex = adapter?.Index;

        try
        {
            await _server.StartAsync(options, _project).ConfigureAwait(true);

            Log.AddNotice(mode == DhcpServerMode.Serve
                ? $"Serving {PlannedDeviceCount} planned device(s) on {adapter?.DisplayName}."
                : "Watching. Nothing will be transmitted.");

            if (mode == DhcpServerMode.Serve && !options.Boot.IsEmpty)
            {
                Log.AddNotice($"Every reply also carries: {options.Boot.Describe()}.");
            }
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

        NoteAssignReply(e);
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
        CancelAssignWait();

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
            UpdateSweepPlan();
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
        if (InterfaceBar.SelectedAdapter?.Nic is not { } nic)
        {
            return null;
        }

        // Any of the adapter's addresses will do: a second address on a second subnet is exactly as
        // much evidence about that subnet's mask as the first is about its own.
        foreach (NicInfo part in nic.PerAddress())
        {
            if (Ipv4Subnet.TryCreate(part.IPv4, part.Mask, out Ipv4Subnet subnet) && subnet.Contains(address))
            {
                return part.Mask!.ToString();
            }
        }

        return null;
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

    private static string ReadRoutedRanges(ProjectStore project)
    {
        try
        {
            return project.Settings.Get(RoutedRangesSetting) ?? string.Empty;
        }
        catch (PersistenceException)
        {
            return string.Empty;
        }
    }

    private void ApplyProject(ProjectStore project)
    {
        ProjectName = project.Project.Name;
        ProjectPath = project.IsInMemory ? null : project.FilePath;
        IsUnsavedLocation = project.IsInMemory;

        RecordBuild(project);

        Plan.Load(project);
        ReloadPlan();
        Scan.RoutedRanges = ReadRoutedRanges(project);
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
