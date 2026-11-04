using NetControl.Core;
using NetControl.Core.Persistence;

namespace NetControl.App.ViewModels;

/// <summary>
/// Filling in the MAC of a plan row that was imported without one (PLAN-IMPORT.md F3).
///
/// <para>A project IP workbook comes off the drawings: names and addresses, no MACs. Those rows sit
/// in the plan awaiting their MAC, never served, until the device turns up - either asking for an
/// address in the request log, or answering at its planned address in a scan - and somebody says
/// "that one is SF101RC01".</para>
///
/// <para><b>It is always somebody saying so.</b> A wrong MAC on a row hands that row's address to
/// the wrong device the next time Serve is armed, so the tool proposes and a person confirms -
/// one row from a menu, or a list of pairings shown in full before "Fill in all". Each fill is
/// written to the record with where the MAC came from.</para>
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// What the "Assign to awaiting plan row" picker should offer for this request, or null - with
    /// the reason in the log - when there is nothing to pick: the row is not about a device, the
    /// device is already planned, or no row is awaiting a MAC.
    /// </summary>
    public IReadOnlyList<AwaitingRowOption>? AwaitingRowsFor(LogEntryViewModel entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (entry.Mac.IsEmpty)
        {
            Log.AddNotice("That row is not about a device, so there is no MAC to fill in.");
            return null;
        }

        if (Plan.IsPlanned(entry.Mac))
        {
            Plan.PlanDevice(entry.Mac);
            Log.AddNotice($"{entry.Mac} is already in the plan - selected its row.");
            return null;
        }

        IReadOnlyList<DeviceRowViewModel> awaiting = Plan.AwaitingMac();
        if (awaiting.Count == 0)
        {
            Log.AddNotice(
                "No plan row is awaiting a MAC.",
                "Rows await one when they were imported from a workbook, or typed with an address and no MAC.");
            return null;
        }

        return [.. awaiting.Select(row => new AwaitingRowOption(row))];
    }

    /// <summary>Fills a request's MAC into the row picked for it. True when the row now has it.</summary>
    public bool AssignToAwaitingRow(LogEntryViewModel entry, DeviceRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(row);

        return !entry.Mac.IsEmpty && FillAwaitingMac(entry.Mac, row, "the request log");
    }

    /// <summary>
    /// Fills a scanned device's MAC into the row awaiting one at the address it answered from. The
    /// menu click is the confirmation: it names one device and the row is found by its address.
    /// </summary>
    public bool FillMacFromScan(ScanResultViewModel result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (!result.CanBePlanned)
        {
            Log.AddNotice($"{result.AddressText} has no MAC this machine could read, so there is none to fill in.",
                result.PlanningObstacle);
            return false;
        }

        if (Plan.AwaitingAt(result.Address) is not { } row)
        {
            Log.AddNotice($"No plan row awaiting a MAC is planned at {result.AddressText}.");
            return false;
        }

        return FillAwaitingMac(result.Mac, row, "a scan");
    }

    /// <summary>
    /// Every scan result that answered at the address of a row awaiting its MAC, paired with that
    /// row - what "Fill in all" will do, for the window to show before it is done. An address two
    /// devices answered on is left out: there is no telling which of them the row means.
    /// </summary>
    public IReadOnlyList<AwaitingMacMatch> AwaitingMacMatches()
    {
        List<AwaitingMacMatch> matches = [];

        foreach (IGrouping<string, ScanResultViewModel> at in Scan.Rows
            .Where(result => result.CanBePlanned && !Plan.IsPlanned(result.Mac))
            .GroupBy(result => result.AddressText, StringComparer.Ordinal))
        {
            List<ScanResultViewModel> answering = [.. at.DistinctBy(result => result.Mac)];
            if (answering.Count == 1 && Plan.AwaitingAt(answering[0].Address) is { } row)
            {
                matches.Add(new AwaitingMacMatch(answering[0], row, new AwaitingRowOption(row).Name));
            }
        }

        if (matches.Count == 0)
        {
            Log.AddNotice(
                "Nothing in the scan results answered at the address of a plan row awaiting its MAC.",
                "Scan the subnet the rows are planned on, from an adapter on that subnet.");
        }

        return matches;
    }

    /// <summary>Applies the pairings the user was shown. Returns how many rows now have their MAC.</summary>
    public int FillMacsFromScan(IReadOnlyList<AwaitingMacMatch> matches)
    {
        ArgumentNullException.ThrowIfNull(matches);

        int filled = matches.Count(match => FillAwaitingMac(match.Result.Mac, match.Row, "a scan"));

        if (matches.Count > 1)
        {
            Log.AddNotice($"Filled in {filled} of {matches.Count} MACs from the scan.");
        }

        return filled;
    }

    /// <summary>
    /// The one place a MAC is written onto an awaiting row. Goes through the grid, as typing it
    /// would, so the row's own checks, the vendor lookup and the write to the file all happen the
    /// same way; then records it.
    /// </summary>
    private bool FillAwaitingMac(MacAddress mac, DeviceRowViewModel row, string source)
    {
        if (Plan.IsPlanned(mac))
        {
            Plan.PlanDevice(mac);
            Log.AddNotice($"{mac} is already in the plan - selected its row. Nothing was changed.");
            return false;
        }

        if (!Plan.AwaitingMac().Contains(row))
        {
            Log.AddNotice("That plan row is no longer awaiting a MAC. Nothing was changed.");
            return false;
        }

        string name = new AwaitingRowOption(row).Name;
        string ip = row.IpText;

        row.MacText = mac.ToString();

        if (row.HasProblem || row.Mac != mac)
        {
            string why = row.Problem ?? "the row could not be saved to the project";
            SetError($"{mac} could not be filled in on {name}: {why}", "Check the row in the plan grid.");
            return false;
        }

        Plan.SelectedRow = row;
        ClearError();

        try
        {
            _project.Events.Append(
                EventSeverity.Info,
                EventCategory.App,
                $"MAC filled in from {source}: {mac} is {name} ({ip}).",
                target: mac.ToString(),
                deviceId: row.Id);
        }
        catch (PersistenceException ex)
        {
            RecordingIsIncomplete = true;
            Log.AddNotice($"Could not record that in the project: {ex.Message}", ex.Remediation);
        }

        Log.AddNotice(
            $"{mac} filled in on {name} at {ip}, from {source}.",
            row.IsServable
                ? "It will be answered in Serve mode."
                : row.ServeNote);

        return true;
    }
}

/// <summary>A scanned device and the plan row awaiting a MAC at the address it answered from.</summary>
public sealed record AwaitingMacMatch(ScanResultViewModel Result, DeviceRowViewModel Row, string RowName);
