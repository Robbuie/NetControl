using System.Collections.ObjectModel;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetControl.Core;
using NetControl.Core.Cip;
using NetControl.Core.Commissioning;
using NetControl.Core.Enip;
using NetControl.Core.Oui;
using NetControl.Core.Persistence;
using NetControl.Core.Reporting;

namespace NetControl.App.ViewModels;

/// <summary>
/// The plan, as an editable grid over <see cref="ProjectStore.Devices"/>.
///
/// <para><b>There is no Save button, and there is no dirty flag.</b> SQLite writes as it goes, so
/// a cell that has been edited is already in the file. The only thing a project can be missing is
/// a location, which is what <see cref="ProjectStore.SaveAs"/> is for. Anything else would mean an
/// unsaved plan could be lost by closing a window, and the whole reason this exists is that
/// somebody's morning of typing MACs must not evaporate.</para>
///
/// <para><b>Errors go in the row.</b> A panel underneath would make the user match a message to a
/// line by eye, and WPF's own <c>DataGrid</c> validation paints a red box with no sentence in it.
/// The row carries its own explanation and its own colour, so the fix is where the mistake is.</para>
///
/// <para><b>Editing while serving is allowed, and is the normal workflow.</b> You watch a device
/// ask, you plan it, and it gets served on its next retransmit - that loop is the point of the
/// live log. <see cref="NetControl.Core.Dhcp.StaticMapPolicy"/> is a concurrent dictionary for
/// exactly this reason; the
/// grid raises <see cref="PlanChanged"/> and the owner rebuilds the policy the receive loop reads.</para>
/// </summary>
public sealed partial class DeviceGridViewModel : ObservableObject
{
    private readonly IOuiLookup _oui;
    private readonly StaticIpCommissioner _commissioner;

    /// <summary>
    /// MACs that have asked for an address this session. A session fact, not a stored one - the
    /// project file records what was served, not who knocked - so it lives here and is cleared
    /// with the project.
    /// </summary>
    private readonly HashSet<MacAddress> _seen = [];

    private ProjectStore? _project;

    /// <summary>
    /// The device a run is currently about, so the progress steps - which arrive from Core knowing
    /// nothing about rows - land against the right device in the record rather than unattributed.
    /// </summary>
    private long? _commissioningDeviceId;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveDeviceCommand))]
    [NotifyCanExecuteChangedFor(nameof(SetStaticCommand))]
    [NotifyCanExecuteChangedFor(nameof(EnableBootpCommand))]
    [NotifyCanExecuteChangedFor(nameof(EnableDhcpCommand))]
    [NotifyPropertyChangedFor(nameof(CanHandBack))]
    private DeviceRowViewModel? _selectedRow;

    /// <summary>
    /// True while a device is being commissioned. One at a time, deliberately: this writes to live
    /// equipment and the log has to read as an account of one operation, not two interleaved.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SetStaticCommand))]
    [NotifyCanExecuteChangedFor(nameof(EnableBootpCommand))]
    [NotifyCanExecuteChangedFor(nameof(EnableDhcpCommand))]
    [NotifyCanExecuteChangedFor(nameof(SetStaticAllCommand))]
    [NotifyPropertyChangedFor(nameof(CanHandBack))]
    private bool _isCommissioning;

    /// <summary>True while Set static is working through the plan. Enables the Stop button.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopBulkCommand))]
    private bool _isBulkRunning;

    /// <summary>Where a bulk run has got to, or what the last one did.</summary>
    [ObservableProperty]
    private string? _bulkStatus;

    /// <summary>The run in progress, so Stop has something to ask. Null when none is.</summary>
    private BulkCommissioner? _bulk;

    /// <summary>
    /// Asked before Set static on all writes anything, with the rows it is about to write to; true
    /// means go ahead. The window sets this to a dialog that names every device.
    ///
    /// <para><b>Null refuses.</b> A run over the whole plan writes to every device on it, and the one
    /// default that cannot surprise anybody is that it does not happen without being confirmed. A
    /// test that wants it to run says so.</para>
    /// </summary>
    public Func<IReadOnlyList<DeviceRowViewModel>, bool>? ConfirmBulk { get; set; }

    /// <summary>
    /// Whether the commissioner may reset a device that will not apply a configuration without one.
    ///
    /// <para>Off by default and surfaced as its own control, because on a running line resetting a
    /// module is an outage. Nobody should discover they authorised one by pressing "Set static".</para>
    /// </summary>
    [ObservableProperty]
    private bool _allowDeviceReset;

    /// <summary>
    /// The last thing the file refused to do. Shown above the grid; the row that caused it also
    /// carries the message, so this is the copy for someone who has already scrolled away.
    /// </summary>
    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _errorRemediation;

    public DeviceGridViewModel(IOuiLookup oui, StaticIpCommissioner? commissioner = null)
    {
        ArgumentNullException.ThrowIfNull(oui);

        _oui = oui;
        _commissioner = commissioner ?? new StaticIpCommissioner();
        _commissioner.Progress += OnCommissionProgress;
    }

    /// <summary>
    /// Raised whenever a row has reached the file. The owner reloads the policy on this, so the
    /// receive loop is serving the plan that is on screen and not the one that was on screen when
    /// the project was opened.
    /// </summary>
    public event EventHandler? PlanChanged;

    /// <summary>
    /// Each step of a commissioning run, for the live log.
    ///
    /// <para>Raised on whatever thread the run is on - the socket work does not come back to the UI
    /// thread on its own - so the owner marshals, exactly as it does for the DHCP server's events.</para>
    /// </summary>
    public event EventHandler<CommissionProgressEventArgs>? CommissionProgress;

    public ObservableCollection<DeviceRowViewModel> Rows { get; } = [];

    /// <summary>
    /// The EtherNet/IP port a commissioning run talks to. Almost always 44818; settable for the
    /// same reason <see cref="EnipSession"/>'s is, so a simulator can be driven on a spare port.
    /// </summary>
    public int CommissionPort { get; set; } = EnipSession.DefaultPort;

    /// <summary>Rows that are complete, valid, and would be answered in Serve mode.</summary>
    public int ServableCount => Rows.Count(row => row.IsServable);

    /// <summary>
    /// The rows Set static on all would write to: saved, servable - so an address and a mask to write
    /// - and not already Verified. Exactly the rule the single Set static uses, minus the rows that
    /// are already done, so nothing reaches a device in bulk that could not be reached one at a time.
    /// </summary>
    public IReadOnlyList<DeviceRowViewModel> ReadyForBulk() =>
        [.. Rows.Where(row => row.Id > 0 && row.IsServable && row.State != DeviceState.Verified
            && row.TryBuild(out DeviceRecord? record)
            && record?.PlannedIp is not null && record.PlannedMask is not null)];

    /// <summary>Points the grid at a project and reads its plan. Replaces everything on screen.</summary>
    public void Load(ProjectStore project)
    {
        ArgumentNullException.ThrowIfNull(project);

        _project = project;
        _seen.Clear();
        ClearError();
        Reload();
    }

    /// <summary>
    /// Re-reads every row from the file. Used after an import, and after anything that changed the
    /// plan from outside the grid.
    /// </summary>
    public void Reload()
    {
        foreach (DeviceRowViewModel row in Rows)
        {
            row.Edited -= OnRowEdited;
        }

        Rows.Clear();

        if (_project is null)
        {
            return;
        }

        IReadOnlySet<long> served = _project.Assignments.ServedDeviceIds();

        // Verified survives closing the project. Only a readback sets it, and the readback is in the
        // record - so reading the record back is the readback, not a guess. A row whose planned
        // address has been edited since is not the device that was verified, and goes back to what it
        // can otherwise claim.
        IReadOnlyDictionary<long, DeviceCommissioning> outcomes = CommissioningRecord.LastOutcomes(_project.Events.All());

        foreach (DeviceRecord record in _project.Devices.All())
        {
            var row = DeviceRowViewModel.FromRecord(record);
            row.State = IsStillVerified(record, outcomes)
                ? DeviceState.Verified
                : StateOf(record.Id, record.Mac, served);
            row.Edited += OnRowEdited;
            Rows.Add(row);
        }

        OnPropertyChanged(nameof(ServableCount));
        SetStaticAllCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Notes that a MAC has asked for an address, so its row can say so. Called for every request,
    /// including ones from devices nobody planned - those simply do not match a row.
    /// </summary>
    public void NoteSeen(MacAddress mac)
    {
        if (mac.IsEmpty || !_seen.Add(mac))
        {
            return;
        }

        foreach (DeviceRowViewModel row in Rows)
        {
            if (row.Mac == mac && row.State == DeviceState.Planned)
            {
                row.State = DeviceState.Seen;
            }
        }
    }

    /// <summary>
    /// Notes that a device was served, which is the one state transition that comes from the wire
    /// rather than from the file. The <c>Assignment</c> row is written by the recorder on the
    /// receive loop; this is the grid catching up without re-reading the whole plan.
    /// </summary>
    public void NoteServed(MacAddress mac)
    {
        foreach (DeviceRowViewModel row in Rows)
        {
            if (row.Mac == mac && row.State < DeviceState.Served)
            {
                row.State = DeviceState.Served;
            }
        }

        SetStaticAllCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Whether the plan already holds this MAC. The plan is keyed on it, so this is exact.</summary>
    public bool IsPlanned(MacAddress mac) => !mac.IsEmpty && Rows.Any(row => row.Mac == mac);

    /// <summary>
    /// Adds a device from outside the grid - a log row, or later a scan result - with its vendor
    /// resolved now and stored.
    ///
    /// <para><b>The address cell is left empty on purpose, and the tool never guesses at one.</b>
    /// An address is a decision somebody made on a drawing: drives at .51, remote I/O at .21, the
    /// HMI where the HMI always goes. A tool that offers the lowest free address offers .1, which
    /// on a real panel is the gateway - so the suggestion would be wrong nearly every time and
    /// wrong in a way that is easy to accept by mistake. Typing four characters is cheap; finding
    /// out on site that a device took an address nobody meant it to is not.</para>
    ///
    /// <para>A MAC already in the plan selects the row that has it rather than adding a second.
    /// <c>Device.Mac</c> is UNIQUE, so the alternative is an exception where the user expected to
    /// be shown where the device already is.</para>
    /// </summary>
    /// <returns>The row, existing or new.</returns>
    public DeviceRowViewModel PlanDevice(MacAddress mac)
    {
        DeviceRowViewModel? existing = Rows.FirstOrDefault(row => row.Mac == mac);
        if (existing is not null)
        {
            SelectedRow = existing;
            return existing;
        }

        DeviceRowViewModel row = NewRow();
        row.MacText = mac.ToString();      // Raises Edited, which resolves the vendor and commits.
        SelectedRow = row;
        return row;
    }

    [RelayCommand]
    private void AddDevice() => SelectedRow = NewRow();

    [RelayCommand(CanExecute = nameof(CanRemoveDevice))]
    private void RemoveDevice()
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        // A row that was never written has nothing to remove from the file, and refusing to drop
        // an empty row somebody added by mistake would be absurd.
        if (row.Id != 0 && _project is not null)
        {
            try
            {
                // Deleted by the MAC the file holds, not the one in the cell. A user who mistyped
                // over a good MAC and then hit Remove means "get rid of this row", and keying off
                // the unparseable text would leave the real row in the plan.
                if (_project.Devices.Get(row.Id) is { } stored)
                {
                    _project.Devices.Delete(stored.Mac);
                }
            }
            catch (PersistenceException ex)
            {
                // The commissioning record already points at this device. Say so and leave the row
                // alone - the record has to keep meaning what it says.
                SetError(ex.Message, ex.Remediation);
                row.Problem = ex.Message;
                return;
            }
        }

        row.Edited -= OnRowEdited;
        Rows.Remove(row);
        SelectedRow = null;
        ClearError();

        OnPropertyChanged(nameof(ServableCount));
        PlanChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Writes the planned address into the selected device and turns BOOTP/DHCP off, then reads it
    /// back.
    ///
    /// <para>This is the half of the job the listener does not do. A device served over BOOTP holds
    /// its address until the next power cycle and then asks again; this is what makes it the
    /// device's own.</para>
    ///
    /// <para>The device is addressed at its planned address, which is where BOOTP just put it, and
    /// that address is the only one anything is sent to. Every step goes into the append-only event
    /// log, because this put packets on live equipment.</para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSetStatic))]
    private async Task SetStaticAsync(CancellationToken cancellationToken)
    {
        if (SelectedRow is not { } row || _project is null || !row.TryBuild(out DeviceRecord? device))
        {
            return;
        }

        if (BuildStaticRequest(device!) is not { } request)
        {
            return;
        }

        IPAddress ip = request.DeviceAddress;

        IsCommissioning = true;
        _commissioningDeviceId = row.Id;

        try
        {
            Record(EventSeverity.Info, row,
                $"Set static requested for {device!.Mac} at {ip}"
                + (AllowDeviceReset ? ", reset allowed." : ", no reset."));

            CommissionResult result = await _commissioner.RunAsync(request, cancellationToken);

            Apply(row, result);
        }
        catch (OperationCanceledException)
        {
            // The window is closing mid-run. The device is in whatever state it reached, and the
            // steps already recorded say which - there is nothing honest to add here.
        }
        finally
        {
            IsCommissioning = false;
            _commissioningDeviceId = null;
        }
    }

    /// <summary>
    /// Set static on every row in <see cref="ReadyForBulk"/>, one device at a time.
    ///
    /// <para>Each row goes through exactly what the single Set static does: the same request, the same
    /// <see cref="Apply"/>, the same event rows attributed to the same device - so there is still only
    /// one way a row becomes Verified. What this adds is the loop, a line in the record either side of
    /// it, and a Stop that takes effect between devices rather than half way through one.</para>
    ///
    /// <para>The confirmation naming every device belongs to the window, for the same reason the
    /// hand-back's does: a view model that puts a dialog on the screen cannot be tested.</para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSetStaticAll))]
    private async Task SetStaticAllAsync(CancellationToken cancellationToken)
    {
        if (_project is null)
        {
            return;
        }

        var work = new List<(DeviceRowViewModel Row, StaticIpRequest Request)>();

        foreach (DeviceRowViewModel row in ReadyForBulk())
        {
            if (row.TryBuild(out DeviceRecord? device) && BuildStaticRequest(device!) is { } request)
            {
                work.Add((row, request));
            }
        }

        if (work.Count == 0)
        {
            BulkStatus = "Nothing to do: every complete row is already Verified.";
            return;
        }

        if (ConfirmBulk is not { } confirm || !confirm([.. work.Select(w => w.Row)]))
        {
            BulkStatus = "Set static on all was not confirmed. Nothing was sent.";
            return;
        }

        var bulk = new BulkCommissioner(_commissioner);

        // Raised on this thread: BulkCommissioner resumes on the caller's context between devices,
        // so the rows can be touched directly here, exactly as the single Set static touches them.
        bulk.DeviceStarting += (_, e) =>
        {
            DeviceRowViewModel row = work[e.Index].Row;
            _commissioningDeviceId = row.Id;
            SelectedRow = row;
            BulkStatus = $"Device {e.Index + 1} of {e.Total}: {row.Mac} at {e.Request.DeviceAddress}...";

            Record(EventSeverity.Info, row,
                $"Set static requested for {row.Mac} at {e.Request.DeviceAddress} (device {e.Index + 1} of {e.Total} "
                + "in a run over the plan" + (AllowDeviceReset ? ", reset allowed)." : ", no reset)."));
        };

        bulk.DeviceFinished += (_, e) => Apply(work[e.Index].Row, e.Result!);

        IsCommissioning = true;
        IsBulkRunning = true;
        _bulk = bulk;

        try
        {
            Record(EventSeverity.Info, row: null,
                $"Set static on all ready devices: {work.Count} device(s), one at a time: "
                + string.Join(", ", work.Select(w => $"{w.Row.Mac} at {w.Request.DeviceAddress}")) + ".");

            BulkCommissionResult result = await bulk.RunAsync([.. work.Select(w => w.Request)], cancellationToken);

            BulkStatus = result.Summary;

            // After the run, with no device id: it is about the run, and each device already has its own.
            _commissioningDeviceId = null;
            Record(
                result.AllVerified ? EventSeverity.Info : EventSeverity.Warn,
                row: null,
                result.Summary,
                new EventDetail()
                    .Add("operation", "setStaticAll")
                    .Add("devices", work.Count)
                    .Add("verified", result.Verified)
                    .Add("notVerified", result.NotVerified)
                    .Add("notStarted", result.NotStarted.Count)
                    .Add("stopped", result.WasStopped));
        }
        catch (OperationCanceledException)
        {
            // The window is closing mid-run. Every device already has its own rows in the record.
        }
        finally
        {
            IsCommissioning = false;
            IsBulkRunning = false;
            _commissioningDeviceId = null;
            _bulk = null;
        }
    }

    /// <summary>Stops a bulk run after the device in progress. Nothing more is sent.</summary>
    [RelayCommand(CanExecute = nameof(IsBulkRunning))]
    private void StopBulk()
    {
        _bulk?.RequestStop();
        BulkStatus = "Stopping after this device - the one in progress is allowed to finish.";
    }

    /// <summary>
    /// Turns BOOTP back on for the selected device, handing it back to whatever server answers on
    /// its segment. See <see cref="HandBackAsync"/>; the two protocols are separate commands so
    /// that neither can be reached by leaving a dropdown where somebody else left it.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanHandBack))]
    private Task EnableBootpAsync(CancellationToken cancellationToken) =>
        HandBackAsync(ConfigMethod.Bootp, cancellationToken);

    /// <summary>Turns DHCP back on for the selected device. See <see cref="HandBackAsync"/>.</summary>
    [RelayCommand(CanExecute = nameof(CanHandBack))]
    private Task EnableDhcpAsync(CancellationToken cancellationToken) =>
        HandBackAsync(ConfigMethod.Dhcp, cancellationToken);

    /// <summary>
    /// The opposite of <see cref="SetStaticAsync"/>: stops the device owning its address and hands
    /// it back to a BOOTP or DHCP server.
    ///
    /// <para><b>Configuration Control and nothing else.</b> The addresses the device is holding are
    /// left exactly as they are - see <see cref="StaticIpRequest.HandBack"/> - so this is not a
    /// move, it is a change of who decides. The device keeps working at the address it has until
    /// its next power cycle, and then asks.</para>
    ///
    /// <para>It is deliberately a separate, explicitly labelled action rather than a mode on Set
    /// static. It is the opposite of the default, it is how a device stops being commissioned, and
    /// it is how a device gets handed back to a plant DHCP server at the end of a job - all three
    /// deserve to be chosen rather than arrived at.</para>
    ///
    /// <para>The device is addressed at its planned address, because that is where it is now, and
    /// that address is the only one anything is sent to.</para>
    /// </summary>
    private async Task HandBackAsync(ConfigMethod method, CancellationToken cancellationToken)
    {
        if (SelectedRow is not { } row || _project is null || !row.TryBuild(out DeviceRecord? device))
        {
            return;
        }

        // No mask is needed and none is used: attribute 5 is not written on a hand-back. A row with
        // an address but no mask is unservable, and is still perfectly reachable to be talked to.
        if (device!.PlannedIp is not { } ip)
        {
            return;
        }

        StaticIpRequest request = StaticIpRequest.HandBack(
            ip,
            method,
            device.Quirks,
            allowReset: AllowDeviceReset,
            port: CommissionPort);

        IsCommissioning = true;
        _commissioningDeviceId = row.Id;

        try
        {
            // Warn rather than Info, even though it is exactly what was asked for. Six months later
            // the question somebody has is "when did this device stop being static", and that line
            // should not have to be picked out of a hundred routine ones.
            Record(EventSeverity.Warn, row,
                $"Enable {Name(method)} requested for {device.Mac} at {ip}. The device will ask for an address "
                + "at its next power cycle."
                + (AllowDeviceReset ? " Reset allowed." : string.Empty));

            CommissionResult result = await _commissioner.RunAsync(request, cancellationToken);

            ApplyHandBack(row, method, result);
        }
        catch (OperationCanceledException)
        {
            // The window is closing mid-run - same as Set static. The steps already recorded say
            // where the device got to, and there is nothing honest to add here.
        }
        finally
        {
            IsCommissioning = false;
            _commissioningDeviceId = null;
        }
    }

    /// <summary>
    /// A verified hand-back is the one success in this app that moves a row <em>backwards</em>.
    ///
    /// <para><see cref="DeviceState.Verified"/> means "read back off the device holding the address
    /// the plan gave it". A device that has just been told to ask for one is not that any more, and
    /// a plan still showing Verified beside it is a plan that lies to the next person to open it.
    /// So the row returns to what it can honestly claim - Served if this session served it, Seen if
    /// it has asked, Planned otherwise.</para>
    /// </summary>
    private void ApplyHandBack(DeviceRowViewModel row, ConfigMethod method, CommissionResult result)
    {
        if (result.IsVerified)
        {
            row.State = StateOf(row.Id, row.Mac, ServedDeviceIds());
            row.Problem = null;
            ClearError();
        }
        else
        {
            SetError(result.Message, result.Remediation);
            row.Problem = result.Message;
        }

        Record(
            result.IsVerified ? EventSeverity.Warn : EventSeverity.Error,
            row,
            result.Message,
            new EventDetail()
                .Add("operation", $"enable{method}")
                .Add("outcome", result.Outcome)
                .Add("wroteToDevice", result.WroteToDevice)
                .Add("resetTheDevice", result.ResetTheDevice)
                .Add("readback", result.Readback?.ToString())
                .Add("reportedMethod", result.ReportedMethod));

        LearnQuirks(row, result);
    }

    /// <summary>
    /// Changes a device's quirks by hand, from the editor. One event row naming what was added and
    /// what was removed, because "who decided this drive needs a reset" is a question the record
    /// should be able to answer.
    /// </summary>
    public void EditQuirks(DeviceRowViewModel row, DeviceQuirks quirks)
    {
        ArgumentNullException.ThrowIfNull(row);

        DeviceQuirks before = row.Quirks;
        if (!ApplyKeepingProblem(row, quirks))
        {
            return;
        }

        string added = DeviceQuirkCatalog.ShortText(quirks & ~before);
        string removed = DeviceQuirkCatalog.ShortText(before & ~quirks);

        Record(
            EventSeverity.Info,
            row,
            $"Quirks for {row.Mac} edited by hand"
                + (added.Length > 0 ? $"; added {added}" : string.Empty)
                + (removed.Length > 0 ? $"; removed {removed}" : string.Empty) + ".",
            new EventDetail()
                .Add("operation", "editQuirks")
                .Add("quirksBefore", (long)before)
                .Add("quirksAfter", (long)quirks));
    }

    /// <summary>
    /// What this attempt proved about the device, written onto its row and into the record. Only
    /// ever adds; see <see cref="QuirkLearning"/>.
    /// </summary>
    private void LearnQuirks(DeviceRowViewModel row, CommissionResult result)
    {
        LearnedQuirks learned = QuirkLearning.Learn(result, row.Quirks);
        if (!learned.Any)
        {
            return;
        }

        DeviceQuirks after = row.Quirks | learned.Flags;
        ApplyKeepingProblem(row, after);

        Record(
            EventSeverity.Info,
            row,
            $"Learned about {row.Mac}: {DeviceQuirkCatalog.ShortText(learned.Flags)} - {string.Join("; ", learned.Reasons)}. "
                + "Untick it under Device quirks if that was a one-off.",
            new EventDetail()
                .Add("operation", "learnQuirks")
                .Add("quirksAfter", (long)after));
    }

    /// <summary>
    /// Writes new quirks through the ordinary edit path, keeping whatever the row was saying. The
    /// edit path revalidates the row's cells, which clears a problem - and the problem a row is
    /// most likely to be showing at this moment is the failed Set static that just taught it.
    /// </summary>
    private bool ApplyKeepingProblem(DeviceRowViewModel row, DeviceQuirks quirks)
    {
        // The edit path also clears the grid's error banner once the row is saved - and the banner
        // is saying why the Set static that taught this quirk failed. Both are put back.
        string? problem = row.Problem;
        string? error = ErrorMessage;
        string? remediation = ErrorRemediation;
        bool changed = row.ApplyQuirks(quirks);

        if (changed && problem is not null && row.Problem is null)
        {
            row.Problem = problem;
        }

        if (changed && error is not null && ErrorMessage is null)
        {
            SetError(error, remediation);
        }

        return changed;
    }

    /// <summary>
    /// Needs an address to talk to, and nothing else.
    ///
    /// <para>Deliberately weaker than <see cref="CanSetStatic"/>, which wants a servable row. A
    /// hand-back writes no address, so there is nothing for a missing mask to make wrong - and the
    /// device most likely to need handing back is one somebody set static months ago and whose plan
    /// row has since been half edited.</para>
    /// </summary>
    /// <remarks>
    /// A property rather than a method so the menu that carries the confirmation can bind its
    /// enablement to this exact rule. A menu item that looks available and then does nothing is a
    /// gesture people repeat, and a second copy of the rule written in XAML is a rule that drifts.
    /// </remarks>
    public bool CanHandBack =>
        !IsCommissioning
        && _project is not null
        && SelectedRow is { Id: > 0 } row
        && row.TryBuild(out DeviceRecord? device)
        && device?.PlannedIp is not null;

    /// <summary>
    /// What the file says was served, or an empty set when it cannot be read. Never throws: this
    /// runs immediately after a write to live equipment, and a project file that has gone read-only
    /// must not turn a completed operation into an exception.
    /// </summary>
    private IReadOnlySet<long> ServedDeviceIds()
    {
        try
        {
            return _project?.Assignments.ServedDeviceIds() ?? new HashSet<long>();
        }
        catch (PersistenceException ex)
        {
            SetError($"Could not re-read what has been served: {ex.Message}", ex.Remediation);
            return new HashSet<long>();
        }
    }

    /// <summary>What to call a configuration method in a sentence somebody reads.</summary>
    private static string Name(ConfigMethod method) => method switch
    {
        ConfigMethod.Bootp => "BOOTP",
        ConfigMethod.Dhcp => "DHCP",
        _ => "static addressing",
    };

    /// <summary>
    /// Only a readback sets <see cref="DeviceState.Verified"/>. Nothing else in the app may, and
    /// that is the whole point of the state: a reply reaching the wire proves a reply reached the
    /// wire, and a CIP success proves the request was accepted. Neither proves the device came up.
    /// </summary>
    private void Apply(DeviceRowViewModel row, CommissionResult result)
    {
        // Whichever way it went, the set of rows Set static on all would touch may have changed.
        SetStaticAllCommand.NotifyCanExecuteChanged();

        if (result.IsVerified)
        {
            row.State = DeviceState.Verified;

            // Clears a failure from an earlier attempt too. A row that reads as broken after the
            // device has been commissioned is a row somebody will act on.
            row.Problem = null;
            ClearError();
        }
        else
        {
            SetError(result.Message, result.Remediation);
            row.Problem = result.Message;
        }

        Record(
            result.IsVerified ? EventSeverity.Info : EventSeverity.Error,
            row,
            result.Message,
            new EventDetail()
                .Add("outcome", result.Outcome)
                .Add("wroteToDevice", result.WroteToDevice)
                .Add("resetTheDevice", result.ResetTheDevice)
                .Add("readback", result.Readback?.ToString())
                .Add("reportedMethod", result.ReportedMethod));

        LearnQuirks(row, result);
    }

    private bool CanSetStaticAll() =>
        !IsCommissioning
        && _project is not null
        && Rows.Any(row => row.Id > 0 && row.IsServable && row.State != DeviceState.Verified);

    /// <summary>
    /// The request for one planned device: addressed at its planned address, which is where BOOTP
    /// just put it, and told to stay there. Null when the row has no address or mask to write.
    /// </summary>
    private StaticIpRequest? BuildStaticRequest(DeviceRecord device)
    {
        if (device.PlannedIp is not { } ip || device.PlannedMask is not { } mask)
        {
            return null;
        }

        return new StaticIpRequest
        {
            // Where the device is now and where it should stay. The common job is not moving a
            // device but making the address it already has permanent.
            DeviceAddress = ip,
            Ip = ip,
            Mask = mask,
            Gateway = device.PlannedGateway,
            Quirks = device.Quirks,
            AllowReset = AllowDeviceReset,
            Port = CommissionPort,
        };
    }

    private bool CanSetStatic() =>
        !IsCommissioning
        && _project is not null
        && SelectedRow is { IsServable: true, Id: > 0 };

    private void OnCommissionProgress(object? sender, CommissionProgressEventArgs e)
    {
        CommissionProgress?.Invoke(this, e);

        // Straight into the record as it happens, not summarised at the end. If the connection
        // drops half way through, what was already sent is the only thing anybody has to go on.
        Record(e.IsFailure ? EventSeverity.Warn : EventSeverity.Info, row: null, e.Message);
    }

    /// <summary>
    /// One line of the commissioning record. Never throws: a project file that has gone read-only
    /// must not be able to abort a write to a device half way through.
    /// </summary>
    private void Record(EventSeverity severity, DeviceRowViewModel? row, string message, EventDetail? detail = null)
    {
        if (_project is null)
        {
            return;
        }

        long? deviceId = row?.Id ?? _commissioningDeviceId;

        try
        {
            _project.Events.Append(
                severity,
                EventCategory.Cip,
                message,
                target: row?.Mac.ToString(),
                deviceId: deviceId is > 0 ? deviceId : null,
                detail: detail);
        }
        catch (PersistenceException ex)
        {
            SetError($"Could not record a commissioning step: {ex.Message}", ex.Remediation);
        }
    }

    private bool CanRemoveDevice() => SelectedRow is not null;

    private DeviceRowViewModel NewRow()
    {
        DeviceRowViewModel row = DeviceRowViewModel.Blank();
        row.Edited += OnRowEdited;
        Rows.Add(row);
        return row;
    }

    /// <summary>
    /// Writes a row that has just been edited.
    ///
    /// <para>Per cell, not per row. WPF commits a text column when the cell loses focus, so this
    /// runs on every field the user leaves - which is what makes a half-typed row show its problem
    /// while it is still half-typed, rather than at the end when the user has moved on.</para>
    /// </summary>
    private void OnRowEdited(object? sender, EventArgs e)
    {
        if (sender is not DeviceRowViewModel row || _project is null)
        {
            return;
        }

        // Vendor is resolved here, at the moment the MAC is entered, and stored with the row. Never
        // on read: the IEEE registry moves, and a project file has to read the same way in six
        // months as it did on the day it was typed. Revalidating after folds it into the record the
        // row is about to hand over - the row built itself before this ran.
        row.Vendor = row.Mac.IsEmpty ? null : _oui.Lookup(row.Mac);
        row.Revalidate();

        if (!row.TryBuild(out DeviceRecord? built) || built is null)
        {
            // Either blank or invalid. The row is already saying which; nothing goes to the file.
            OnPropertyChanged(nameof(ServableCount));
            return;
        }

        if (Rows.Any(other => !ReferenceEquals(other, row) && other.Mac == built.Mac))
        {
            row.Problem = $"{built.Mac} is already in the plan. The plan is keyed on the MAC, so it can only "
                + "appear once.";
            OnPropertyChanged(nameof(ServableCount));
            return;
        }

        try
        {
            if (row.Id == 0)
            {
                row.Id = _project.Devices.Upsert(built);
            }
            else if (!_project.Devices.Update(built with { Id = row.Id }))
            {
                // The row was deleted underneath us - by another window on the same file, or by a
                // hand-edit. Insert it again rather than lose what was typed.
                row.Id = _project.Devices.Upsert(built with { Id = 0 });
            }

            ClearError();
        }
        catch (PersistenceException ex)
        {
            SetError(ex.Message, ex.Remediation);
            row.Problem = ex.Message;
            OnPropertyChanged(nameof(ServableCount));
            return;
        }

        // Revalidate so the row's cached record carries the id it was just given; without it a
        // second edit to a brand new row would try to insert it again.
        row.Revalidate();

        // Planning a device that has already been heard from is the normal way round - you watch it
        // ask, then you plan it - so the row has to arrive already knowing it has been seen rather
        // than waiting for the device to ask again.
        if (row.State == DeviceState.Planned && _seen.Contains(row.Mac))
        {
            row.State = DeviceState.Seen;
        }

        OnPropertyChanged(nameof(ServableCount));
        SetStaticAllCommand.NotifyCanExecuteChanged();
        PlanChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Whether the record says this device's last finished operation was a verified Set static, at
    /// the address the plan still gives it.
    /// </summary>
    private static bool IsStillVerified(DeviceRecord record, IReadOnlyDictionary<long, DeviceCommissioning> outcomes) =>
        outcomes.TryGetValue(record.Id, out DeviceCommissioning? last)
        && last.Outcome == CommissioningOutcome.Verified
        && record.PlannedIp is { } planned
        && (last.Readback is null || last.Readback.StartsWith($"ip={planned} ", StringComparison.Ordinal));

    private DeviceState StateOf(long id, MacAddress mac, IReadOnlySet<long> served)
    {
        if (served.Contains(id))
        {
            return DeviceState.Served;
        }

        return _seen.Contains(mac) ? DeviceState.Seen : DeviceState.Planned;
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
