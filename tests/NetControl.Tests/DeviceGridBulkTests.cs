using System.Net;
using NetControl.App.ViewModels;
using NetControl.Core.Commissioning;
using NetControl.Core.Oui;
using NetControl.Core.Persistence;
using Xunit;

namespace NetControl.Tests;

/// <summary>
/// Set static on all, from the grid. The single Set static is tested in
/// <see cref="DeviceGridCommissioningTests"/>; these are about which rows a run touches, that it
/// does nothing without a confirmation, and that the record still reads as one operation per device.
/// </summary>
public sealed class DeviceGridBulkTests : IDisposable
{
    private readonly ProjectStore _project = ProjectStore.CreateInMemory("Test");

    public void Dispose() => _project.Dispose();

    [Fact]
    public async Task SetsEveryReadyRowAndRecordsEachOneAgainstItsOwnDevice()
    {
        await using SimulatedAdapter adapter = await SimulatedAdapter.StartAsync();

        DeviceGridViewModel grid = Grid(adapter);
        DeviceRowViewModel first = Planned(grid, "00:1D:9C:C7:B0:70");
        DeviceRowViewModel second = Planned(grid, "00:1D:9C:C7:B0:71");
        grid.ConfirmBulk = _ => true;

        await grid.SetStaticAllCommand.ExecuteAsync(null);

        Assert.Equal(DeviceState.Verified, first.State);
        Assert.Equal(DeviceState.Verified, second.State);

        IReadOnlyList<EventRecord> record = _project.Events.All();
        Assert.Contains(record, e => e.DeviceId == first.Id && e.Detail?.Contains("Verified", StringComparison.Ordinal) == true);
        Assert.Contains(record, e => e.DeviceId == second.Id && e.Detail?.Contains("Verified", StringComparison.Ordinal) == true);
        Assert.Contains(record, e => e.Detail?.Contains("\"operation\":\"setStaticAll\"", StringComparison.Ordinal) == true);
        Assert.Contains("2 verified", grid.BulkStatus, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>No confirmation, no run.</b> The safe default for an operation that writes to every device
    /// in the plan is that it does not happen - and nothing at all reaches a device.
    /// </summary>
    [Fact]
    public async Task DoesNothingWithoutAConfirmation()
    {
        await using SimulatedAdapter adapter = await SimulatedAdapter.StartAsync();

        DeviceGridViewModel grid = Grid(adapter);
        DeviceRowViewModel row = Planned(grid, "00:1D:9C:C7:B0:70");

        Assert.Null(grid.ConfirmBulk);
        await grid.SetStaticAllCommand.ExecuteAsync(null);

        Assert.NotEqual(DeviceState.Verified, row.State);
        Assert.True(adapter.IsStillDynamic);
        Assert.Contains("not confirmed", grid.BulkStatus, StringComparison.Ordinal);

        grid.ConfirmBulk = _ => false;
        await grid.SetStaticAllCommand.ExecuteAsync(null);

        Assert.True(adapter.IsStillDynamic);
    }

    /// <summary>The confirmation is shown exactly the rows that will be written - no more.</summary>
    [Fact]
    public void OffersOnlyRowsThatAreCompleteAndNotYetVerified()
    {
        var grid = new DeviceGridViewModel(OuiDatabase.Empty);
        grid.Load(_project);

        DeviceRowViewModel ready = Planned(grid, "00:1D:9C:C7:B0:70");
        DeviceRowViewModel done = Planned(grid, "00:1D:9C:C7:B0:71");
        done.State = DeviceState.Verified;

        grid.AddDeviceCommand.Execute(null);
        DeviceRowViewModel macOnly = grid.Rows[^1];
        macOnly.MacText = "00:1D:9C:C7:B0:72";

        IReadOnlyList<DeviceRowViewModel> offered = grid.ReadyForBulk();

        Assert.Equal(new[] { ready }, offered);
    }

    [Fact]
    public void IsUnavailableWhileAnotherRunIsGoing()
    {
        var grid = new DeviceGridViewModel(OuiDatabase.Empty);
        grid.Load(_project);
        Planned(grid, "00:1D:9C:C7:B0:70");

        Assert.True(grid.SetStaticAllCommand.CanExecute(null));

        grid.IsCommissioning = true;
        Assert.False(grid.SetStaticAllCommand.CanExecute(null));
    }

    private static DeviceGridViewModel Grid(SimulatedAdapter adapter)
    {
        var commissioner = new StaticIpCommissioner
        {
            ConnectTimeout = TimeSpan.FromSeconds(2),
            VerifyAttempts = 5,
            VerifyDelay = TimeSpan.FromMilliseconds(20),
        };

        return new DeviceGridViewModel(OuiDatabase.Empty, commissioner) { CommissionPort = adapter.Port };
    }

    /// <summary>
    /// A complete row at loopback. Two rows here plan the same address, which a real plan would not -
    /// but every test device is the one simulator on loopback, and the grid does not refuse it.
    /// </summary>
    private DeviceRowViewModel Planned(DeviceGridViewModel grid, string mac)
    {
        if (grid.Rows.Count == 0)
        {
            grid.Load(_project);
        }

        grid.AddDeviceCommand.Execute(null);
        DeviceRowViewModel row = grid.Rows[^1];

        row.MacText = mac;
        row.IpText = IPAddress.Loopback.ToString();
        row.MaskText = "255.255.255.0";

        return row;
    }
}
