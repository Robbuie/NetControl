using System.Net;
using NetControl.App.ViewModels;
using NetControl.Core.Commissioning;
using NetControl.Core.DeviceHealth;
using NetControl.Core.Oui;
using NetControl.Core.Persistence;
using Xunit;
using SimMethod = NetControl.DeviceSim.ConfigMethod;

namespace NetControl.Tests;

/// <summary>
/// The plan's actions beyond one device at a time: the selection, Remove and Undo, editing many
/// rows, the clipboard, the filter, and the read-only check.
/// </summary>
public sealed class DeviceGridActionsTests : IDisposable
{
    private readonly ProjectStore _project = ProjectStore.CreateInMemory("Test");
    private readonly DeviceGridViewModel _grid = new(OuiDatabase.Empty);

    public DeviceGridActionsTests() => _grid.Load(_project);

    public void Dispose() => _project.Dispose();

    /// <summary>
    /// Selecting a row from code - planning a device from the log, a bulk run moving down - means that
    /// row alone; the grid widens the selection again itself.
    /// </summary>
    [Fact]
    public void TheCurrentRowAloneIsTheSelectionUntilTheGridSaysOtherwise()
    {
        DeviceRowViewModel a = Row("00:1D:9C:C7:B0:70", "192.168.1.51");
        DeviceRowViewModel b = Row("00:1D:9C:C7:B0:71", "192.168.1.52");

        _grid.SelectedRow = a;
        Assert.Equal(new[] { a }, _grid.Selection);
        Assert.False(_grid.HasMultipleSelected);

        _grid.SetSelection([a, b]);
        Assert.Equal(2, _grid.SelectionCount);
        Assert.True(_grid.HasMultipleSelected);

        // Moving the current row inside the selection keeps it; outside replaces it.
        _grid.SelectedRow = b;
        Assert.Equal(2, _grid.SelectionCount);

        DeviceRowViewModel c = Row("00:1D:9C:C7:B0:72", "192.168.1.53");
        _grid.SelectedRow = c;
        Assert.Equal(new[] { c }, _grid.Selection);
    }

    [Fact]
    public void RemovingSeveralRowsAsksFirstAndUndoPutsThemBack()
    {
        DeviceRowViewModel a = Row("00:1D:9C:C7:B0:70", "192.168.1.51");
        DeviceRowViewModel b = Row("00:1D:9C:C7:B0:71", "192.168.1.52");
        Row("00:1D:9C:C7:B0:72", "192.168.1.53");
        a.Notes = "keep me";

        _grid.SelectedRow = a;
        _grid.SetSelection([a, b]);

        // No confirmation, nothing removed.
        _grid.RemoveDeviceCommand.Execute(null);
        Assert.Equal(3, _project.Devices.Count);

        IReadOnlyList<DeviceRowViewModel>? asked = null;
        _grid.ConfirmRemove = rows => { asked = rows; return true; };
        _grid.RemoveDeviceCommand.Execute(null);

        Assert.Equal(2, asked?.Count);
        Assert.Equal(1, _project.Devices.Count);
        Assert.Single(_grid.Rows);
        Assert.True(_grid.CanUndoRemove);

        _grid.UndoRemoveCommand.Execute(null);

        Assert.Equal(3, _project.Devices.Count);
        Assert.Equal(3, _grid.Rows.Count);
        Assert.Contains(_grid.Rows, row => row.Notes == "keep me" && row.IpText == "192.168.1.51");
        Assert.False(_grid.CanUndoRemove);
    }

    /// <summary>A device planned again since it was removed is not written over by Undo.</summary>
    [Fact]
    public void UndoLeavesOutADeviceThatHasBeenPlannedAgain()
    {
        DeviceRowViewModel a = Row("00:1D:9C:C7:B0:70", "192.168.1.51");
        _grid.SelectedRow = a;
        _grid.RemoveDeviceCommand.Execute(null);

        DeviceRowViewModel again = Row("00:1D:9C:C7:B0:70", "192.168.1.99");

        _grid.UndoRemoveCommand.Execute(null);

        DeviceRecord stored = Assert.Single(_project.Devices.All());
        Assert.Equal(IPAddress.Parse("192.168.1.99"), stored.PlannedIp);
        Assert.NotNull(_grid.ErrorMessage);
        Assert.Equal("192.168.1.99", again.IpText);
    }

    [Fact]
    public void ClearingTheMacPutsARowBackToAwaitingItsMac()
    {
        DeviceRowViewModel row = Row("00:1D:9C:C7:B0:70", "192.168.1.51");
        _grid.SelectedRow = row;

        _grid.ClearMacCommand.Execute(null);

        DeviceRecord stored = Assert.Single(_project.Devices.All());
        Assert.True(stored.IsAwaitingMac);
        Assert.Equal(IPAddress.Parse("192.168.1.51"), stored.PlannedIp);
        Assert.Contains(_project.Events.All(), e => e.Message.Contains("awaiting its MAC again", StringComparison.Ordinal));
        Assert.False(_grid.ClearMacCommand.CanExecute(null));
    }

    [Fact]
    public void SetsTheSameValuesOnEverySelectedRowAndRefusesATypo()
    {
        DeviceRowViewModel a = Row("00:1D:9C:C7:B0:70", "192.168.1.51");
        DeviceRowViewModel b = Row("00:1D:9C:C7:B0:71", "192.168.1.52");
        a.GatewayText = "192.168.1.254";
        _grid.SelectedRow = a;
        _grid.SetSelection([a, b]);

        Assert.NotNull(_grid.SetOnSelection(mask: "255.255.0.", gateway: null));
        Assert.Equal("255.255.255.0", a.MaskText);

        Assert.Null(_grid.SetOnSelection(mask: "255.255.0.0", gateway: string.Empty, panelRef: "CP-101"));

        Assert.All(_project.Devices.All(), device =>
        {
            Assert.Equal(IPAddress.Parse("255.255.0.0"), device.PlannedMask);
            Assert.Null(device.PlannedGateway);
            Assert.Equal("CP-101", device.PanelRef);
        });
    }

    /// <summary>The next address after the row's, stepping over what the plan already has.</summary>
    [Fact]
    public void DuplicatesARowAtTheNextFreeAddress()
    {
        DeviceRowViewModel source = Row("00:1D:9C:C7:B0:70", "192.168.1.51");
        source.ApplyCells(gateway: "192.168.1.1", panelRef: "CP-101", role: "VFD");
        Row("00:1D:9C:C7:B0:71", "192.168.1.52");
        _grid.SelectedRow = source;

        _grid.DuplicateNextCommand.Execute(null);

        DeviceRowViewModel added = _grid.SelectedRow!;
        Assert.Equal(1, _grid.Rows.IndexOf(added));
        Assert.Equal("192.168.1.53", added.IpText);
        Assert.Equal(string.Empty, added.MacText);
        Assert.Equal("CP-101", added.PanelRef);
        Assert.Equal("VFD", added.Role);
        Assert.True(added.Id > 0);
        Assert.Contains(_project.Devices.All(), d => d.IsAwaitingMac && d.PlannedIp!.Equals(IPAddress.Parse("192.168.1.53")));
    }

    [Fact]
    public void NoNextAddressAtTheTopOfTheSubnet()
    {
        DeviceRowViewModel row = Row("00:1D:9C:C7:B0:70", "192.168.1.254");

        Assert.Null(_grid.NextFreeAddress(row));
    }

    [Fact]
    public void CopiesTheSelectionAndPastesItBackAsNewRows()
    {
        DeviceRowViewModel a = Row("00:1D:9C:C7:B0:70", "192.168.1.51");
        a.HostName = "drive1";
        _grid.SelectedRow = a;

        string text = _grid.CopyText();
        Assert.StartsWith("MAC\tIP\tMask", text, StringComparison.Ordinal);
        Assert.Contains("00:1D:9C:C7:B0:70\t192.168.1.51\t255.255.255.0\t\tdrive1", text, StringComparison.Ordinal);

        // The same MAC again is refused in the row, exactly as typing it would be; a new one goes in.
        string status = _grid.Paste(text + "00:1D:9C:C7:B0:7A\t192.168.1.60\t255.255.255.0\t\tdrive2\r\n");

        Assert.Contains("Pasted 2 row(s); 1 could not be stored", status, StringComparison.Ordinal);
        Assert.Equal(2, _project.Devices.Count);
        Assert.Contains(_project.Devices.All(), d => d.HostName == "drive2");
        Assert.Equal(2, _grid.SelectionCount);
    }

    [Fact]
    public void FiltersByTextAndByHowFarARowHasGot()
    {
        DeviceRowViewModel drive = Row("00:1D:9C:C7:B0:70", "192.168.1.51");
        drive.Role = "VFD conveyor";
        DeviceRowViewModel awaiting = Row(string.Empty, "192.168.1.52");
        int raised = 0;
        _grid.FilterChanged += (_, _) => raised++;

        _grid.FilterText = "conveyor";
        Assert.True(_grid.Matches(drive));
        Assert.False(_grid.Matches(awaiting));
        Assert.Equal("Showing 1 of 2", _grid.FilterSummary);

        _grid.FilterText = string.Empty;
        _grid.RowFilter = PlanRowFilter.AwaitingMac;
        Assert.False(_grid.Matches(drive));
        Assert.True(_grid.Matches(awaiting));

        // A row nobody has typed into yet is always shown, or Add device would add an invisible row.
        _grid.AddDeviceCommand.Execute(null);
        Assert.True(_grid.Matches(_grid.Rows[^1]));

        _grid.ClearFilterCommand.Execute(null);
        Assert.False(_grid.IsFiltering);
        Assert.True(raised >= 3);
    }

    /// <summary>
    /// The check reads and never writes: a device still on BOOTP is reported as one, the row is told,
    /// and the device is exactly as it was.
    /// </summary>
    [Fact]
    public async Task CheckReportsADeviceThatIsStillABootpClientWithoutChangingIt()
    {
        await using SimulatedAdapter adapter = await SimulatedAdapter.StartAsync();
        adapter.Device.Ip = IPAddress.Loopback;

        DeviceGridViewModel grid = CheckingGrid(adapter);
        DeviceRowViewModel row = Row(grid, adapter.Device.MacString, IPAddress.Loopback.ToString());
        grid.SelectedRow = row;

        await grid.CheckSelectedCommand.ExecuteAsync(null);

        Assert.Equal("differs", row.CheckText);
        Assert.True(row.CheckIsBad);
        Assert.Contains("BOOTP client", row.CheckTooltip, StringComparison.Ordinal);
        Assert.True(adapter.IsStillDynamic);
        Assert.NotEqual(DeviceState.Verified, row.State);
        Assert.Contains(_project.Events.All(), e => e.DeviceId == row.Id && e.Message.Contains("read only", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CheckSaysADeviceMatchesWhenItHoldsThePlanStatically()
    {
        await using SimulatedAdapter adapter = await SimulatedAdapter.StartAsync();
        adapter.Device.Ip = IPAddress.Loopback;
        adapter.Device.Method = SimMethod.Static;

        DeviceGridViewModel grid = CheckingGrid(adapter);
        DeviceRowViewModel row = Row(grid, adapter.Device.MacString, IPAddress.Loopback.ToString());
        grid.SelectedRow = row;

        await grid.CheckSelectedCommand.ExecuteAsync(null);

        Assert.Equal("matches", row.CheckText);
        Assert.False(row.CheckIsBad);
        Assert.Contains("1 match the plan", grid.BulkStatus, StringComparison.Ordinal);
    }

    /// <summary>The MAC the device reports is compared too: an answer at the address is not proof of identity.</summary>
    [Fact]
    public async Task CheckNoticesTheWrongDeviceAtThePlannedAddress()
    {
        await using SimulatedAdapter adapter = await SimulatedAdapter.StartAsync();
        adapter.Device.Ip = IPAddress.Loopback;
        adapter.Device.Method = SimMethod.Static;

        DeviceGridViewModel grid = CheckingGrid(adapter);
        DeviceRowViewModel row = Row(grid, "00:1D:9C:00:00:01", IPAddress.Loopback.ToString());
        grid.SelectedRow = row;

        await grid.CheckSelectedCommand.ExecuteAsync(null);

        Assert.Equal("differs", row.CheckText);
        Assert.Contains("not the device this row plans", row.CheckTooltip, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetsStaticOnTheSelectedRowsOnly()
    {
        await using SimulatedAdapter adapter = await SimulatedAdapter.StartAsync();

        var commissioner = new StaticIpCommissioner
        {
            ConnectTimeout = TimeSpan.FromSeconds(2),
            VerifyAttempts = 5,
            VerifyDelay = TimeSpan.FromMilliseconds(20),
        };
        var grid = new DeviceGridViewModel(OuiDatabase.Empty, commissioner) { CommissionPort = adapter.Port };
        grid.Load(_project);

        DeviceRowViewModel a = Row(grid, "00:1D:9C:C7:B0:70", IPAddress.Loopback.ToString());
        DeviceRowViewModel b = Row(grid, "00:1D:9C:C7:B0:71", IPAddress.Loopback.ToString());
        DeviceRowViewModel left = Row(grid, "00:1D:9C:C7:B0:72", IPAddress.Loopback.ToString());

        grid.SelectedRow = a;
        Assert.False(grid.SetStaticSelectedCommand.CanExecute(null));
        grid.SetSelection([a, b]);
        Assert.True(grid.SetStaticSelectedCommand.CanExecute(null));

        IReadOnlyList<DeviceRowViewModel>? confirmed = null;
        grid.ConfirmBulk = rows => { confirmed = rows; return true; };

        await grid.SetStaticSelectedCommand.ExecuteAsync(null);

        Assert.Equal(new[] { a, b }, confirmed);
        Assert.Equal(DeviceState.Verified, a.State);
        Assert.Equal(DeviceState.Verified, b.State);
        Assert.NotEqual(DeviceState.Verified, left.State);
        Assert.Contains(_project.Events.All(), e => e.Detail?.Contains("\"operation\":\"setStaticSelected\"", StringComparison.Ordinal) == true);
    }

    private DeviceGridViewModel CheckingGrid(SimulatedAdapter adapter)
    {
        var grid = new DeviceGridViewModel(
            OuiDatabase.Empty,
            healthReader: new DeviceHealthReader { Port = adapter.Port, ConnectTimeout = TimeSpan.FromSeconds(2) });
        grid.Load(_project);
        return grid;
    }

    private DeviceRowViewModel Row(string mac, string ip) => Row(_grid, mac, ip);

    private static DeviceRowViewModel Row(DeviceGridViewModel grid, string mac, string ip)
    {
        grid.AddDeviceCommand.Execute(null);
        DeviceRowViewModel row = grid.Rows[^1];
        row.ApplyCells(mac: mac, ip: ip, mask: "255.255.255.0");
        return row;
    }
}
