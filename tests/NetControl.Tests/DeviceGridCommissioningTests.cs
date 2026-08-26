using System.Net;
using NetControl.App.ViewModels;
using NetControl.Core.Commissioning;
using NetControl.Core.Oui;
using NetControl.Core.Persistence;
using Xunit;
using SimQuirk = NetControl.DeviceSim.Quirk;

namespace NetControl.Tests;

/// <summary>
/// "Set static" from the grid, end to end: a plan row, a real socket, a simulated adapter, and the
/// commissioning record afterwards.
///
/// <para>The assertions that matter are about what the record says. A tool that puts packets on
/// live plant equipment and cannot afterwards say what it sent is not a commissioning tool.</para>
/// </summary>
public sealed class DeviceGridCommissioningTests : IDisposable
{
    private readonly ProjectStore _project = ProjectStore.CreateInMemory("Test");

    public void Dispose() => _project.Dispose();

    /// <summary>
    /// <b>Only a readback sets Verified.</b> Nothing else in the app may, which is the entire point
    /// of the state: a BOOTP reply reaching the wire proves a reply reached the wire, and a CIP
    /// success proves a request was accepted. Neither proves the device came up.
    /// </summary>
    [Fact]
    public async Task AVerifiedReadbackIsWhatSetsTheRowToVerified()
    {
        await using SimulatedAdapter adapter = await SimulatedAdapter.StartAsync();

        (DeviceGridViewModel grid, DeviceRowViewModel row) = PlannedAt(adapter);

        await grid.SetStaticCommand.ExecuteAsync(null);

        Assert.Equal(DeviceState.Verified, row.State);
        Assert.Null(grid.ErrorMessage);
        Assert.True(adapter.IsStatic);
    }

    /// <summary>
    /// The device returns success for the write and discards it. The row must not end up Verified,
    /// because a row that says Verified is what somebody writes on a drawing.
    /// </summary>
    [Fact]
    public async Task ADeviceThatLiesAboutTheWriteNeverReachesVerified()
    {
        await using SimulatedAdapter adapter = await SimulatedAdapter.StartAsync(SimQuirk.LiesAboutWriteSuccess);

        (DeviceGridViewModel grid, DeviceRowViewModel row) = PlannedAt(adapter);

        await grid.SetStaticCommand.ExecuteAsync(null);

        Assert.NotEqual(DeviceState.Verified, row.State);
        Assert.NotNull(grid.ErrorMessage);
        Assert.True(row.HasProblem);
    }

    /// <summary>
    /// Every step, as it happened, in the append-only table - not a summary written at the end. If
    /// the connection drops half way through, what was already sent is all anybody has to go on.
    /// </summary>
    [Fact]
    public async Task WritesTheWholeSequenceIntoTheCommissioningRecord()
    {
        await using SimulatedAdapter adapter = await SimulatedAdapter.StartAsync();

        (DeviceGridViewModel grid, DeviceRowViewModel row) = PlannedAt(adapter);

        await grid.SetStaticCommand.ExecuteAsync(null);

        IReadOnlyList<EventRecord> record = _project.Events.All();

        Assert.All(record, e => Assert.Equal(EventCategory.Cip, e.Category));
        Assert.Contains(record, e => e.Message.Contains("Configuration Capability", StringComparison.Ordinal));
        Assert.Contains(record, e => e.Message.Contains("attribute 3", StringComparison.Ordinal));
        Assert.Contains(record, e => e.Message.Contains("attribute 5", StringComparison.Ordinal));

        // And the outcome, attributed to the device it was about.
        Assert.Contains(record, e => e.DeviceId == row.Id && e.Detail is not null
            && e.Detail.Contains("Verified", StringComparison.Ordinal));
    }

    /// <summary>
    /// A device pinned by switches: the record has to be able to say that nothing was sent to it.
    /// After a failed attempt on live equipment, "we changed nothing" is the sentence that matters.
    /// </summary>
    [Fact]
    public async Task RecordsThatNothingWasWrittenToADevicePinnedBySwitches()
    {
        await using SimulatedAdapter adapter = await SimulatedAdapter.StartAsync(SimQuirk.HardwarePinnedAddress);

        (DeviceGridViewModel grid, DeviceRowViewModel row) = PlannedAt(adapter);

        await grid.SetStaticCommand.ExecuteAsync(null);

        Assert.NotEqual(DeviceState.Verified, row.State);
        Assert.Contains("switches", grid.ErrorRemediation!, StringComparison.OrdinalIgnoreCase);

        Assert.Contains(
            _project.Events.All(),
            e => e.Detail is not null && e.Detail.Contains("\"wroteToDevice\":false", StringComparison.Ordinal));
    }

    /// <summary>Resetting a module is an outage, so it is a separate decision that defaults to no.</summary>
    [Fact]
    public async Task DoesNotResetADeviceUnlessTheBoxIsTicked()
    {
        await using SimulatedAdapter adapter = await SimulatedAdapter.StartAsync(SimQuirk.RequiresResetToApply);

        (DeviceGridViewModel grid, DeviceRowViewModel row) = PlannedAt(adapter);
        Assert.False(grid.AllowDeviceReset);

        await grid.SetStaticCommand.ExecuteAsync(null);

        Assert.NotEqual(DeviceState.Verified, row.State);
        Assert.Contains(
            _project.Events.All(),
            e => e.Detail is not null && e.Detail.Contains("\"resetTheDevice\":false", StringComparison.Ordinal));

        grid.AllowDeviceReset = true;
        await grid.SetStaticCommand.ExecuteAsync(null);

        Assert.Equal(DeviceState.Verified, row.State);
    }

    /// <summary>The run narrates itself to the live log while it is happening.</summary>
    [Fact]
    public async Task ReportsEachStepAsItHappens()
    {
        await using SimulatedAdapter adapter = await SimulatedAdapter.StartAsync();

        (DeviceGridViewModel grid, _) = PlannedAt(adapter);

        List<CommissionStep> steps = [];
        grid.CommissionProgress += (_, e) => steps.Add(e.Step);

        await grid.SetStaticCommand.ExecuteAsync(null);

        Assert.Contains(CommissionStep.ReadCapability, steps);
        Assert.Contains(CommissionStep.WriteConfigMethod, steps);
        Assert.Contains(CommissionStep.Finished, steps);
    }

    /// <summary>
    /// A row that is not a complete, valid plan row has no address to write, and a row that has
    /// never reached the file has no device to attribute the record to.
    /// </summary>
    [Fact]
    public void WillNotCommissionARowThatIsNotReadyToBeServed()
    {
        var grid = new DeviceGridViewModel(OuiDatabase.Empty);
        grid.Load(_project);

        Assert.False(grid.SetStaticCommand.CanExecute(null));

        grid.AddDeviceCommand.Execute(null);
        DeviceRowViewModel row = grid.Rows[0];

        // A MAC alone: stored, but with no address it can never be served or commissioned.
        row.MacText = "00:1D:9C:C7:B0:70";
        Assert.False(grid.SetStaticCommand.CanExecute(null));

        row.IpText = "127.0.0.1";
        row.MaskText = "255.255.255.0";
        Assert.True(grid.SetStaticCommand.CanExecute(null));
    }

    /// <summary>One device at a time. Two interleaved runs would make the record unreadable.</summary>
    [Fact]
    public void WillNotStartASecondRunWhileOneIsGoing()
    {
        var grid = new DeviceGridViewModel(OuiDatabase.Empty);
        grid.Load(_project);
        grid.AddDeviceCommand.Execute(null);

        DeviceRowViewModel row = grid.Rows[0];
        row.MacText = "00:1D:9C:C7:B0:70";
        row.IpText = "127.0.0.1";
        row.MaskText = "255.255.255.0";

        Assert.True(grid.SetStaticCommand.CanExecute(null));

        grid.IsCommissioning = true;
        Assert.False(grid.SetStaticCommand.CanExecute(null));
    }

    /// <summary>
    /// A plan row for the simulated adapter, addressed where the test can actually reach it.
    /// </summary>
    private (DeviceGridViewModel Grid, DeviceRowViewModel Row) PlannedAt(SimulatedAdapter adapter)
    {
        var commissioner = new StaticIpCommissioner
        {
            ConnectTimeout = TimeSpan.FromSeconds(2),
            VerifyAttempts = 5,
            VerifyDelay = TimeSpan.FromMilliseconds(20),
        };

        var grid = new DeviceGridViewModel(OuiDatabase.Empty, commissioner) { CommissionPort = adapter.Port };
        grid.Load(_project);

        grid.AddDeviceCommand.Execute(null);
        DeviceRowViewModel row = grid.Rows[0];

        row.MacText = "00:1D:9C:C7:B0:70";
        row.IpText = IPAddress.Loopback.ToString();
        row.MaskText = "255.255.255.0";

        grid.SelectedRow = row;
        return (grid, row);
    }
}
