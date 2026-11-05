using System.Net;
using System.Net.Sockets;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetControl.Core;
using NetControl.Core.Cip;
using NetControl.Core.DeviceHealth;
using NetControl.Core.Enip;
using NetControl.Core.Persistence;
using NetControl.Core.Plan;
using NetControl.Core.Reachability;

namespace NetControl.App.ViewModels;

/// <summary>
/// The plan's everyday actions beyond one device at a time: acting on several selected rows,
/// spreadsheet-style copy and paste, filling a value down, a filter, an undo for Remove, and a
/// read-only check of what a device actually holds.
///
/// <para>Each is one method here with one CanExecute rule, and the window shows it in as many
/// places as it is wanted - the toolbar, the row's right-click menu and a key - so the three can
/// never disagree about when it is available.</para>
/// </summary>
public sealed partial class DeviceGridViewModel
{
    /// <summary>What the last Remove took out of the file, ready to be put back. One level.</summary>
    private readonly List<DeviceRecord> _removed = [];

    /// <summary>Free text the visible rows must contain somewhere. Blank shows everything.</summary>
    [ObservableProperty]
    private string _filterText = string.Empty;

    /// <summary>Which rows to show by how far they have got.</summary>
    [ObservableProperty]
    private PlanRowFilter _rowFilter = PlanRowFilter.All;

    /// <summary>True while a read-only check is running over the selection.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(SetStaticCommand))]
    [NotifyCanExecuteChangedFor(nameof(SetStaticSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(SetStaticAllCommand))]
    private bool _isChecking;

    /// <summary>
    /// Raised when the filter changes and the window has to re-filter its view of <see cref="Rows"/>.
    /// The view model holds the rule (<see cref="Matches"/>); the window owns the collection view,
    /// because a collection view is a WPF type.
    /// </summary>
    public event EventHandler? FilterChanged;

    /// <summary>Raised when <see cref="Selection"/> changes, for anything whose availability follows it.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>
    /// Asked before more than one row is removed, with the rows; true means go ahead. Null refuses,
    /// as <see cref="ConfirmBulk"/> does - a test that wants it to happen says so. A single row is
    /// removed without asking, as it always has been, and Undo is there for both.
    /// </summary>
    public Func<IReadOnlyList<DeviceRowViewModel>, bool>? ConfirmRemove { get; set; }

    /// <summary>The rows the multi-row actions act on: every selected row, or the current one alone.</summary>
    public IReadOnlyList<DeviceRowViewModel> Selection =>
        _selectedRows.Count > 0 ? _selectedRows
        : SelectedRow is { } row ? [row]
        : [];

    /// <summary>How many rows are selected - for "Set static on 4 selected...".</summary>
    public int SelectionCount => Selection.Count;

    /// <summary>True when more than one row is selected, which is when the "selected" actions appear.</summary>
    public bool HasMultipleSelected => Selection.Count > 1;

    /// <summary>The opposite, for the one-device buttons that make way for the "selected" ones.</summary>
    public bool IsSingleSelection => !HasMultipleSelected;

    /// <summary>Whether Undo has a removal to put back.</summary>
    public bool CanUndoRemove => _removed.Count > 0 && _project is not null;

    /// <summary>"Showing 12 of 60" while a filter hides rows; empty when nothing is hidden.</summary>
    public string FilterSummary
    {
        get
        {
            if (!IsFiltering)
            {
                return string.Empty;
            }

            int shown = Rows.Count(Matches);
            return $"Showing {shown} of {Rows.Count}";
        }
    }

    /// <summary>True while the filter could hide anything.</summary>
    public bool IsFiltering => !string.IsNullOrWhiteSpace(FilterText) || RowFilter != PlanRowFilter.All;

    /// <summary>
    /// Replaces the selection with the grid's own, which the window passes in whenever it changes.
    /// The current row (<see cref="SelectedRow"/>) is left to the grid's binding.
    /// </summary>
    public void SetSelection(IEnumerable<DeviceRowViewModel> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        _selectedRows = [.. rows.Distinct()];
        RaiseSelectionChanged();
    }

    /// <summary>
    /// A new current row that is not part of the selection replaces it. The grid widens it again
    /// with <see cref="SetSelection"/>; anything else that sets the current row - planning a device
    /// from the log, a bulk run moving down the plan - means that one row.
    /// </summary>
    partial void OnSelectedRowChanged(DeviceRowViewModel? value)
    {
        if (value is null || !_selectedRows.Contains(value))
        {
            _selectedRows = value is null ? [] : [value];
        }

        RaiseSelectionChanged();
    }

    private void RaiseSelectionChanged()
    {
        OnPropertyChanged(nameof(Selection));
        OnPropertyChanged(nameof(SelectionCount));
        OnPropertyChanged(nameof(HasMultipleSelected));
        OnPropertyChanged(nameof(IsSingleSelection));
        RemoveDeviceCommand.NotifyCanExecuteChanged();
        SetStaticSelectedCommand.NotifyCanExecuteChanged();
        CheckSelectedCommand.NotifyCanExecuteChanged();
        ClearMacCommand.NotifyCanExecuteChanged();
        DuplicateNextCommand.NotifyCanExecuteChanged();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------------ undo

    /// <summary>
    /// Puts back the rows the last Remove took out. A row whose MAC - or, awaiting its MAC, whose
    /// address - has been planned again since is left out rather than written over the new one.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUndoRemove))]
    private void UndoRemove()
    {
        if (_project is null || _removed.Count == 0)
        {
            return;
        }

        var skipped = new List<string>();

        foreach (DeviceRecord record in _removed)
        {
            bool clash = record.Mac.IsEmpty
                ? record.PlannedIp is { } ip && Rows.Any(r => r.TryBuild(out DeviceRecord? other) && ip.Equals(other?.PlannedIp))
                : Rows.Any(r => r.Mac == record.Mac);

            if (clash)
            {
                skipped.Add(record.DisplayName ?? record.Mac.ToString());
                continue;
            }

            try
            {
                _project.Devices.Upsert(record);
            }
            catch (PersistenceException ex)
            {
                skipped.Add($"{record.DisplayName ?? record.Mac.ToString()} ({ex.Message})");
            }
        }

        _removed.Clear();
        OnPropertyChanged(nameof(CanUndoRemove));
        UndoRemoveCommand.NotifyCanExecuteChanged();

        Reload();
        PlanChanged?.Invoke(this, EventArgs.Empty);

        if (skipped.Count > 0)
        {
            SetError(
                $"Not put back, because the plan has it again since: {string.Join(", ", skipped)}.",
                "Edit the row that is there now instead.");
        }
        else
        {
            ClearError();
        }
    }

    // ------------------------------------------------------------------ editing many rows

    /// <summary>
    /// Takes the MAC off the selected rows, so each goes back to awaiting its MAC at its planned
    /// address - for a device swapped out on the panel. A row with no planned address is left
    /// alone: with neither it could never be matched to anything.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanClearMac))]
    private void ClearMac()
    {
        int changed = 0;

        foreach (DeviceRowViewModel row in Selection.ToList())
        {
            if (row.Mac.IsEmpty || string.IsNullOrWhiteSpace(row.IpText))
            {
                continue;
            }

            MacAddress before = row.Mac;
            row.ApplyCells(mac: string.Empty);

            if (row.Problem is null)
            {
                changed++;
                row.State = DeviceState.Planned;
                Record(EventSeverity.Info, row,
                    $"MAC {before} taken off the plan row at {row.IpText}; it is awaiting its MAC again.",
                    new EventDetail().Add("operation", "clearMac").Add("macBefore", before.ToString()));
            }
        }

        if (changed > 0)
        {
            BulkStatus = $"Took the MAC off {changed} row(s); each is awaiting its MAC again.";
        }
    }

    private bool CanClearMac() => Selection.Any(row => !row.Mac.IsEmpty && !string.IsNullOrWhiteSpace(row.IpText));

    /// <summary>
    /// Sets the same cells on every selected row as one edit each. A null leaves a cell alone; an
    /// empty string clears it. Address cells are checked first, so a typo is refused once here
    /// rather than turning every selected row red.
    /// </summary>
    /// <returns>Null when done, or why nothing was changed.</returns>
    public string? SetOnSelection(string? mask = null, string? gateway = null, string? panelRef = null, string? role = null)
    {
        if (BadAddress(mask, "subnet mask") is { } badMask)
        {
            return badMask;
        }

        if (BadAddress(gateway, "gateway") is { } badGateway)
        {
            return badGateway;
        }

        foreach (DeviceRowViewModel row in Selection.ToList())
        {
            row.ApplyCells(mask: mask?.Trim(), gateway: gateway?.Trim(), panelRef: panelRef, role: role);
        }

        return null;
    }

    /// <summary>
    /// Adds a row after the current one with the next free address on its subnet, and the same
    /// mask, gateway, panel and role. No MAC: it is a planned device awaiting one.
    ///
    /// <para>This is not the tool picking an address - the user picked the one this follows on from,
    /// and asked for the next. Anything already in the plan is stepped over, and so are the network
    /// and broadcast addresses.</para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDuplicateNext))]
    private void DuplicateNext()
    {
        if (SelectedRow is not { } source)
        {
            return;
        }

        if (NextFreeAddress(source) is not { } next)
        {
            SetError(
                $"There is no free address after {source.IpText.Trim()} on its subnet.",
                "Check the mask on the row, or type the address in yourself.");
            return;
        }

        DeviceRowViewModel row = DeviceRowViewModel.Blank();
        row.Edited += OnRowEdited;

        int at = Rows.IndexOf(source);
        Rows.Insert(at < 0 ? Rows.Count : at + 1, row);

        row.ApplyCells(
            ip: next.ToString(),
            mask: source.MaskText,
            gateway: source.GatewayText,
            panelRef: source.PanelRef,
            role: source.Role);

        SelectedRow = row;
    }

    private bool CanDuplicateNext() =>
        _project is not null && SelectedRow is { } row
        && IPAddress.TryParse(row.IpText.Trim(), out IPAddress? ip) && ip.AddressFamily == AddressFamily.InterNetwork;

    /// <summary>The next address after the row's on its subnet that nothing in the plan has, or null.</summary>
    internal IPAddress? NextFreeAddress(DeviceRowViewModel source)
    {
        if (!IPAddress.TryParse(source.IpText.Trim(), out IPAddress? ip) || ip.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }

        IPAddress mask = IPAddress.TryParse(source.MaskText.Trim(), out IPAddress? m) && m.AddressFamily == AddressFamily.InterNetwork
            ? m
            : IPAddress.Parse("255.255.255.0");

        uint maskBits = ToUInt(mask);
        uint network = ToUInt(ip) & maskBits;
        uint broadcast = network | ~maskBits;

        var used = new HashSet<uint>();
        foreach (DeviceRowViewModel row in Rows)
        {
            if (IPAddress.TryParse(row.IpText.Trim(), out IPAddress? planned) && planned.AddressFamily == AddressFamily.InterNetwork)
            {
                used.Add(ToUInt(planned));
            }
        }

        for (uint candidate = ToUInt(ip) + 1; candidate < broadcast && candidate > network; candidate++)
        {
            if (!used.Contains(candidate))
            {
                return FromUInt(candidate);
            }
        }

        return null;
    }

    // ------------------------------------------------------------------ clipboard

    /// <summary>The selected rows as spreadsheet text, header first - what Ctrl+C puts on the clipboard.</summary>
    public string CopyText() => CopyText(Selection);

    /// <summary>These rows as spreadsheet text, in plan order, with Vendor and State after the editable cells.</summary>
    public string CopyText(IEnumerable<DeviceRowViewModel> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        HashSet<DeviceRowViewModel> wanted = [.. rows];
        IEnumerable<IReadOnlyList<string?>> lines = Rows
            .Where(wanted.Contains)
            .Select(row => (IReadOnlyList<string?>)[.. row.Cells, row.Vendor, row.State.ToString()]);

        return PlanClipboard.Write(lines, ["Vendor", "State"]);
    }

    /// <summary>
    /// Adds the rows in a block of spreadsheet text to the plan, each as if it had been typed. Rows
    /// that would not store show why, in red, exactly as a typed one does; nothing is overwritten.
    /// </summary>
    /// <returns>A sentence saying what happened, for the status line.</returns>
    public string Paste(string? text)
    {
        IReadOnlyList<PlanCsvRow> pasted = PlanClipboard.Read(text);

        if (pasted.Count == 0)
        {
            return "Nothing to paste: the clipboard has no rows on it.";
        }

        if (_project is null)
        {
            return "Open or start a project before pasting into the plan.";
        }

        var added = new List<DeviceRowViewModel>();

        foreach (PlanCsvRow cells in pasted)
        {
            if (cells.IsBlank)
            {
                continue;
            }

            DeviceRowViewModel row = DeviceRowViewModel.Blank();
            row.Edited += OnRowEdited;
            Rows.Add(row);

            row.ApplyCells(
                cells.Mac, cells.Ip, cells.Mask, cells.Gateway,
                cells.HostName, cells.PanelRef, cells.Role, cells.Notes);

            added.Add(row);
        }

        if (added.Count == 0)
        {
            return "Nothing to paste: every row on the clipboard is blank.";
        }

        SelectedRow = added[0];
        SetSelection(added);

        int bad = added.Count(row => row.HasProblem);
        return bad == 0
            ? $"Pasted {added.Count} row(s) into the plan."
            : $"Pasted {added.Count} row(s); {bad} could not be stored and say why in red.";
    }

    // ------------------------------------------------------------------ filter

    /// <summary>
    /// Whether a row is shown under the current filter. A row nobody has typed into yet is always
    /// shown - otherwise Add device would add a row the user cannot see.
    /// </summary>
    public bool Matches(DeviceRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (row.Id == 0 && row.IsBlank)
        {
            return true;
        }

        bool state = RowFilter switch
        {
            PlanRowFilter.AwaitingMac => row.Mac.IsEmpty && !string.IsNullOrWhiteSpace(row.IpText),
            PlanRowFilter.NotServable => !row.IsServable,
            PlanRowFilter.Problems => row.HasProblem || row.CheckIsBad,
            PlanRowFilter.NotVerified => row.State != DeviceState.Verified,
            PlanRowFilter.Verified => row.State == DeviceState.Verified,
            _ => true,
        };

        if (!state)
        {
            return false;
        }

        string text = FilterText.Trim();
        if (text.Length == 0)
        {
            return true;
        }

        return Contains(row.MacText, text) || Contains(row.IpText, text) || Contains(row.HostName, text)
            || Contains(row.PanelRef, text) || Contains(row.Role, text) || Contains(row.Notes, text)
            || Contains(row.Vendor, text);
    }

    [RelayCommand]
    private void ClearFilter()
    {
        FilterText = string.Empty;
        RowFilter = PlanRowFilter.All;
    }

    partial void OnFilterTextChanged(string value) => RaiseFilterChanged();

    partial void OnRowFilterChanged(PlanRowFilter value) => RaiseFilterChanged();

    private void RaiseFilterChanged()
    {
        OnPropertyChanged(nameof(IsFiltering));
        OnPropertyChanged(nameof(FilterSummary));
        FilterChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool Contains(string? haystack, string needle) =>
        haystack is not null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ read-only check

    /// <summary>
    /// Reads each selected device's address settings over EtherNet/IP and says whether they match
    /// the plan. <b>Nothing is written</b> - this is Get_Attribute_Single and nothing else, the same
    /// read the Diagnostics tab makes - and the row's state does not change: Verified still comes
    /// only from a Set static's readback.
    ///
    /// <para>What it answers is the question somebody has walking up to a panel that may have been
    /// commissioned by someone else: is each device holding the address the plan gives it, is it
    /// static or will it ask again at the next power cycle, and is it even the device the plan
    /// meant.</para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCheckSelected))]
    private async Task CheckSelectedAsync(CancellationToken cancellationToken)
    {
        List<(DeviceRowViewModel Row, DeviceRecord Device)> work = [.. Selection
            .Select(row => (Row: row, Ok: row.TryBuild(out DeviceRecord? device), Device: device))
            .Where(x => x.Ok && x.Device?.PlannedIp is not null)
            .Select(x => (x.Row, x.Device!))];

        if (work.Count == 0)
        {
            return;
        }

        DeviceHealthReader reader = _healthReader ?? new DeviceHealthReader { Port = CommissionPort };
        IsChecking = true;
        int matched = 0;

        try
        {
            for (int i = 0; i < work.Count; i++)
            {
                (DeviceRowViewModel row, DeviceRecord device) = work[i];
                BulkStatus = $"Checking {i + 1} of {work.Count}: {device.PlannedIp}...";

                PlanCheck check = await CheckOneAsync(reader, device, cancellationToken).ConfigureAwait(true);

                row.CheckText = check.Text;
                row.CheckTooltip = check.Sentence;
                row.CheckIsBad = !check.Matches;
                matched += check.Matches ? 1 : 0;

                Record(
                    check.Matches ? EventSeverity.Info : EventSeverity.Warn,
                    row,
                    $"Checked {device.PlannedIp} (read only): {check.Sentence}",
                    new EventDetail().Add("operation", "checkAgainstPlan").Add("matches", check.Matches));
            }

            BulkStatus = $"Checked {work.Count} device(s), read only: {matched} match the plan, {work.Count - matched} do not.";
        }
        catch (OperationCanceledException)
        {
            // The window is closing. Nothing was written, so there is nothing to account for.
        }
        finally
        {
            IsChecking = false;
            RaiseFilterChanged();
        }
    }

    private bool CanCheckSelected() =>
        !IsCommissioning && !IsChecking && _project is not null
        && Selection.Any(row => row.TryBuild(out DeviceRecord? device) && device?.PlannedIp is not null);

    /// <summary>One device's read, turned into a verdict against its plan row.</summary>
    internal static async Task<PlanCheck> CheckOneAsync(
        DeviceHealthReader reader, DeviceRecord device, CancellationToken cancellationToken)
    {
        IPAddress ip = device.PlannedIp!;
        DeviceHealthReport report;

        try
        {
            report = await reader.ReadAsync(ip, device.PlannedMask, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is EnipException or ReachabilityException or SocketException or TimeoutException or System.IO.IOException)
        {
            return new PlanCheck(false, "no answer",
                $"Nothing answered EtherNet/IP at {ip}: {ex.Message} A device that does not speak EtherNet/IP "
                + "cannot be checked this way - ping it instead.");
        }

        var differences = new List<string>();

        if (!device.Mac.IsEmpty && report.Ports.Count > 0
            && report.Ports.All(port => port.Mac is { } mac && mac != device.Mac))
        {
            differences.Add($"it reports MAC {report.Ports[0].Mac}, not {device.Mac} - it is not the device this row plans");
        }

        if (report.Configuration is { } held)
        {
            if (!held.Ip.Equals(ip))
            {
                differences.Add($"it holds {held.Ip}");
            }

            if (device.PlannedMask is { } mask && !held.Mask.Equals(mask))
            {
                differences.Add($"its mask is {held.Mask}, the plan says {mask}");
            }

            if (device.PlannedGateway is { } gateway && !held.Gateway.Equals(gateway))
            {
                differences.Add($"its gateway is {Show(held.Gateway)}, the plan says {gateway}");
            }
        }
        else
        {
            differences.Add("it would not say what address it holds");
        }

        if (report.Method is { } method && method != ConfigMethod.Static)
        {
            differences.Add($"it is still a {(method == ConfigMethod.Bootp ? "BOOTP" : "DHCP")} client and will ask "
                + "for an address at its next power cycle - Set static makes it keep this one");
        }

        string who = report.ProductName is { Length: > 0 } product ? $"{product} at {ip}" : $"The device at {ip}";

        return differences.Count == 0
            ? new PlanCheck(true, "matches", $"{who} holds the planned address, mask and gateway, statically.")
            : new PlanCheck(false, "differs", $"{who} differs from the plan: {string.Join("; ", differences)}.");
    }

    private static string Show(IPAddress address) => address.Equals(IPAddress.Any) ? "none" : address.ToString();

    // ------------------------------------------------------------------ history

    /// <summary>
    /// Everything the project's record holds against this row's device, oldest first: what was
    /// served, every Set static step, checks, learned quirks. Empty for a row never written, or when
    /// the file cannot be read.
    /// </summary>
    public IReadOnlyList<EventRecord> HistoryOf(DeviceRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (_project is null || row.Id <= 0)
        {
            return [];
        }

        try
        {
            return _project.Events.ForDevice(row.Id);
        }
        catch (PersistenceException ex)
        {
            SetError($"Could not read this device's history: {ex.Message}", ex.Remediation);
            return [];
        }
    }

    // ------------------------------------------------------------------ helpers

    private static string? BadAddress(string? text, string what)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return IPAddress.TryParse(text.Trim(), out IPAddress? parsed) && parsed.AddressFamily == AddressFamily.InterNetwork
            && text.Trim().Count(c => c == '.') == 3
            ? null
            : $"'{text.Trim()}' is not an IPv4 {what}. Nothing was changed.";
    }

    private static uint ToUInt(IPAddress address)
    {
        byte[] b = address.GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }

    private static IPAddress FromUInt(uint value) =>
        new([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);
}

/// <summary>Which rows the plan shows, by how far each has got.</summary>
public enum PlanRowFilter
{
    All,
    AwaitingMac,
    NotServable,
    Problems,
    NotVerified,
    Verified,
}

/// <summary>The filter choices as the window lists them.</summary>
public static class PlanRowFilters
{
    public static IReadOnlyList<PlanRowFilterOption> All { get; } =
    [
        new(PlanRowFilter.All, "All rows"),
        new(PlanRowFilter.AwaitingMac, "Awaiting MAC"),
        new(PlanRowFilter.NotServable, "Not servable"),
        new(PlanRowFilter.Problems, "Problems"),
        new(PlanRowFilter.NotVerified, "Not verified"),
        new(PlanRowFilter.Verified, "Verified"),
    ];
}

/// <summary>One filter choice: its value and what the list calls it.</summary>
public sealed record PlanRowFilterOption(PlanRowFilter Value, string Label);

/// <summary>What a read-only check of one device found.</summary>
/// <param name="Matches">True when the device holds the plan's settings, statically.</param>
/// <param name="Text">The grid cell: "matches", "differs", "no answer".</param>
/// <param name="Sentence">What exactly, for the tooltip and the record.</param>
public sealed record PlanCheck(bool Matches, string Text, string Sentence);
