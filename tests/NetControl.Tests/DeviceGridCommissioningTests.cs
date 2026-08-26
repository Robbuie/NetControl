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
    /// Handing a device back is the one success in this app that moves a row <em>backwards</em>.
    /// Verified means "read back off the device holding the address the plan gave it", and a device
    /// that has just been told to ask for one is not that - a plan still showing Verified beside it
    /// is a plan that lies to the next person who opens the file.
    /// </summary>
    [Fact]
    public async Task HandingADeviceBackToBootpTakesTheRowBackOffVerified()
    {
        await using SimulatedAdapter adapter = await SimulatedAdapter.StartAsync();

        (DeviceGridViewModel grid, DeviceRowViewModel row) = PlannedAt(adapter);

        await grid.SetStaticCommand.ExecuteAsync(null);
        Assert.Equal(DeviceState.Verified, row.State);

        IPAddress held = adapter.ConfiguredIp;

        await grid.EnableBootpCommand.ExecuteAsync(null);

        Assert.NotEqual(DeviceState.Verified, row.State);
        Assert.True(adapter.IsStillDynamic);
        Assert.Null(grid.ErrorMessage);

        // Configuration Control and nothing else: the device is still holding the address it had.
        Assert.Equal(held, adapter.ConfiguredIp);
    }

    /// <summary>
    /// Six months later the question is "when did this device stop being static". That line must
    /// not have to be picked out of a hundred routine ones, so a hand-back is recorded as a warning
    /// and says what will happen rather than only what was sent.
    /// </summary>
    [Fact]
    public async Task RecordsAHandBackAsSomethingWorthFinding()
    {
        await using SimulatedAdapter adapter = await SimulatedAdapter.StartAsync();

        (DeviceGridViewModel grid, DeviceRowViewModel row) = PlannedAt(adapter);

        await grid.EnableDhcpCommand.ExecuteAsync(null);

        IReadOnlyList<EventRecord> record = _project.Events.All();

        Assert.Contains(
            record,
            e => e.Severity == EventSeverity.Warn
                && e.Message.Contains("next power cycle", StringComparison.Ordinal));

        Assert.Contains(
            record,
            e => e.DeviceId == row.Id && e.Detail is not null
                && e.Detail.Contains("\"operation\":\"enableDhcp\"", StringComparison.Ordinal));
    }

    /// <summary>
    /// A hand-back writes no address, so a missing mask cannot make it wrong - and the device most
    /// likely to need handing back is one somebody set static months ago whose plan row has been
    /// half edited since. Set static still refuses the same row, because it has an address to write
    /// and no mask to write it with.
    /// </summary>
    [Fact]
    public void CanHandADeviceBackWithoutAMaskEvenThoughItCannotBeServed()
    {
        var grid = new DeviceGridViewModel(OuiDatabase.Empty);
        grid.Load(_project);
        grid.AddDeviceCommand.Execute(null);

        DeviceRowViewModel row = grid.Rows[0];
        row.MacText = "00:1D:9C:C7:B0:70";
        grid.SelectedRow = row;

        // A MAC alone: there is no address to talk to, so there is nothing to hand back.
        Assert.False(grid.CanHandBack);
        Assert.False(grid.EnableBootpCommand.CanExecute(null));

        row.IpText = "127.0.0.1";

        Assert.True(grid.CanHandBack);
        Assert.False(row.IsServable);
        Assert.False(grid.SetStaticCommand.CanExecute(null));
    }

    /// <summary>One device at a time, whichever direction it is going.</summary>
    [Fact]
    public void WillNotHandADeviceBackWhileAnotherRunIsGoing()
    {
        var grid = new DeviceGridViewModel(OuiDatabase.Empty);
        grid.Load(_project);
        grid.AddDeviceCommand.Execute(null);

        DeviceRowViewModel row = grid.Rows[0];
        row.MacText = "00:1D:9C:C7:B0:70";
        row.IpText = "127.0.0.1";
        grid.SelectedRow = row;

        Assert.True(grid.EnableDhcpCommand.CanExecute(null));

        grid.IsCommissioning = true;
        Assert.False(grid.EnableDhcpCommand.CanExecute(null));
        Assert.False(grid.CanHandBack);
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
