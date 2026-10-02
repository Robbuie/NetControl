using System.Net;
using NetControl.App.ViewModels;
using NetControl.Core.DeviceHealth;
using NetControl.Core.Oui;
using NetControl.Core.Persistence;
using NetControl.Core.Reachability;
using Xunit;
using SimQuirk = NetControl.DeviceSim.Quirk;

namespace NetControl.Tests;

/// <summary>
/// The Diagnostics tab as the window uses it: what reaches the screen, what reaches the grid, and
/// what reaches the append-only record. Ping is faked; the CIP read runs against the simulator on
/// loopback.
/// </summary>
public sealed class DiagnosticsViewModelTests : IDisposable
{
    private readonly ProjectStore _project = ProjectStore.CreateInMemory("Test");

    public void Dispose() => _project.Dispose();

    /// <summary>Ping plan fills in the Reach column row by row, and writes one record row for the sweep.</summary>
    [Fact]
    public async Task PingingThePlanFillsInEachRowAndRecordsTheSweepOnce()
    {
        var pinger = new FakePinger().Answers("192.168.1.51", PingStatus.Replied);
        (DiagnosticsViewModel diagnostics, DeviceGridViewModel grid) = Build(pinger);

        DeviceRowViewModel answers = Planned(grid, "00:1D:9C:C7:B0:70", "192.168.1.51");
        DeviceRowViewModel silent = Planned(grid, "00:1D:9C:C7:B0:71", "192.168.1.52");

        await diagnostics.PingPlanCommand.ExecuteAsync(null);

        Assert.Equal(PingStatus.Replied, answers.Reach?.Status);
        Assert.Equal("2 ms", answers.ReachText);
        Assert.True(silent.ReachIsSilent);
        Assert.Equal("no reply", silent.ReachText);

        EventRecord row = Assert.Single(_project.Events.All());
        Assert.Equal(EventCategory.Scan, row.Category);
        Assert.Contains("1 answered, 1 did not", row.Message, StringComparison.Ordinal);
    }

    /// <summary>A row with no address is skipped, not refused - a plan being typed is not an error.</summary>
    [Fact]
    public async Task SkipsRowsWithNoAddressYet()
    {
        var pinger = new FakePinger();
        (DiagnosticsViewModel diagnostics, DeviceGridViewModel grid) = Build(pinger);

        grid.AddDeviceCommand.Execute(null);
        grid.Rows[^1].MacText = "00:1D:9C:C7:B0:70";
        Planned(grid, "00:1D:9C:C7:B0:71", "192.168.1.52");

        await diagnostics.PingPlanCommand.ExecuteAsync(null);

        Assert.All(pinger.Asked, address => Assert.Equal(IPAddress.Parse("192.168.1.52"), address));
    }

    /// <summary>
    /// A planned address that is its own subnet's broadcast refuses the whole sweep - with nothing
    /// sent, and the reason on the tab.
    /// </summary>
    [Fact]
    public async Task APlannedBroadcastAddressRefusesTheSweepAndSendsNothing()
    {
        var pinger = new FakePinger();
        (DiagnosticsViewModel diagnostics, DeviceGridViewModel grid) = Build(pinger);

        Planned(grid, "00:1D:9C:C7:B0:70", "192.168.1.51");
        Planned(grid, "00:1D:9C:C7:B0:71", "192.168.1.255");

        await diagnostics.PingPlanCommand.ExecuteAsync(null);

        Assert.Empty(pinger.Asked);
        Assert.Contains("broadcast", diagnostics.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(_project.Events.All());
    }

    [Fact]
    public async Task RefusesSomethingThatIsNotAnAddressWithoutSendingAnything()
    {
        var pinger = new FakePinger();
        (DiagnosticsViewModel diagnostics, _) = Build(pinger);

        diagnostics.TargetText = "192.168.1";
        await diagnostics.PingTargetCommand.ExecuteAsync(null);

        Assert.Empty(pinger.Asked);
        Assert.Contains("not an IPv4 address", diagnostics.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PingsOneTargetAndSaysWhatHappened()
    {
        var pinger = new FakePinger().Answers("192.168.1.51", PingStatus.Replied);
        (DiagnosticsViewModel diagnostics, _) = Build(pinger);

        diagnostics.SetTarget(IPAddress.Parse("192.168.1.51"), IPAddress.Parse("255.255.255.0"), "Conveyor 3 drive");
        await diagnostics.PingTargetCommand.ExecuteAsync(null);

        Assert.Contains("answered", diagnostics.PingResult, StringComparison.Ordinal);
        Assert.Equal("Conveyor 3 drive", diagnostics.TargetName);
        Assert.Equal("192.168.1.51", Assert.Single(_project.Events.All()).Target);
    }

    /// <summary>Typing over the target forgets the name and mask that belonged to the old one.</summary>
    [Fact]
    public void TypingANewAddressForgetsTheOldTargetsName()
    {
        (DiagnosticsViewModel diagnostics, _) = Build(new FakePinger());

        diagnostics.SetTarget(IPAddress.Parse("192.168.1.51"), name: "Conveyor 3 drive");
        diagnostics.TargetText = "192.168.1.60";

        Assert.Null(diagnostics.TargetName);
    }

    /// <summary>
    /// The CIP read reaches the screen - title, port, findings - and the record, filed under Cip
    /// with the device's serial in the detail.
    /// </summary>
    [Fact]
    public async Task ReadsADevicesHealthAndRecordsIt()
    {
        await using SimulatedAdapter adapter = await SimulatedAdapter.StartAsync(SimQuirk.DuplexMismatch);
        (DiagnosticsViewModel diagnostics, _) = Build(new FakePinger(), adapter);

        diagnostics.SetTarget(SimulatedAdapter.Address);
        await diagnostics.ReadHealthCommand.ExecuteAsync(null);

        Assert.True(diagnostics.HasHealth);
        Assert.Contains("SIM-ENBT/A", diagnostics.HealthTitle, StringComparison.Ordinal);
        Assert.Single(diagnostics.Ports);
        Assert.Contains(diagnostics.Findings, f => f.Message.Contains("half duplex", StringComparison.Ordinal));
        Assert.Contains("First read", diagnostics.HealthComparison, StringComparison.Ordinal);

        EventRecord row = Assert.Single(_project.Events.All());
        Assert.Equal(EventCategory.Cip, row.Category);
        Assert.Equal(EventSeverity.Warn, row.Severity);
        Assert.Contains("00C0FFEE", row.Detail, StringComparison.Ordinal);
    }

    /// <summary>The second read of the same device is compared with the first, and says so.</summary>
    [Fact]
    public async Task ASecondReadOfTheSameDeviceIsCompared()
    {
        await using SimulatedAdapter adapter = await SimulatedAdapter.StartAsync();
        (DiagnosticsViewModel diagnostics, _) = Build(new FakePinger(), adapter);

        diagnostics.SetTarget(SimulatedAdapter.Address);
        await diagnostics.ReadHealthCommand.ExecuteAsync(null);
        await Task.Delay(20);
        await diagnostics.ReadHealthCommand.ExecuteAsync(null);

        Assert.Contains("compared with the read at", diagnostics.HealthComparison, StringComparison.Ordinal);
    }

    /// <summary>Nothing listening: Core's own sentence on the tab, and a Warn in the record.</summary>
    [Fact]
    public async Task SaysSoWhenTheDeviceDoesNotAnswerOverEthernetIp()
    {
        var reader = new DeviceHealthReader { Port = 1, ConnectTimeout = TimeSpan.FromSeconds(1) };
        var grid = new DeviceGridViewModel(OuiDatabase.Empty);
        grid.Load(_project);
        var diagnostics = new DiagnosticsViewModel(
            new ImmediateDispatcher(), () => _project, grid, new FakePinger(), healthReader: reader);

        diagnostics.SetTarget(IPAddress.Loopback);
        await diagnostics.ReadHealthCommand.ExecuteAsync(null);

        Assert.NotNull(diagnostics.ErrorMessage);
        Assert.False(diagnostics.HasHealth);
        Assert.Equal(EventSeverity.Warn, Assert.Single(_project.Events.All()).Severity);
    }

    /// <summary>This PC's own adapter: asked of Windows, not of the network, so nothing is recorded.</summary>
    [Fact]
    public void ReadsThisPcsAdapterTwiceAndSaysWhetherItsCableIsCounting()
    {
        var grid = new DeviceGridViewModel(OuiDatabase.Empty);
        grid.Load(_project);

        long errors = 3;
        var clock = new TestTimeProvider();
        var diagnostics = new DiagnosticsViewModel(
            new ImmediateDispatcher(),
            () => _project,
            grid,
            new FakePinger(),
            selectedAdapter: () => Nics.Healthy(),
            readNicCounters: index => new NetControl.Core.Interfaces.NicCounters
            {
                Index = index,
                Name = "Ethernet",
                ReadUtc = clock.GetUtcNow(),
                ReceivedWithErrors = errors,
            });

        diagnostics.ReadAdapterCountersCommand.Execute(null);
        Assert.Contains("read again", diagnostics.AdapterCountersText, StringComparison.Ordinal);
        Assert.False(diagnostics.AdapterHasNewErrors);

        errors = 9;
        clock.Advance(TimeSpan.FromSeconds(20));
        diagnostics.ReadAdapterCountersCommand.Execute(null);

        Assert.True(diagnostics.AdapterHasNewErrors);
        Assert.Empty(_project.Events.All());
    }

    private (DiagnosticsViewModel Diagnostics, DeviceGridViewModel Grid) Build(
        FakePinger pinger, SimulatedAdapter? adapter = null)
    {
        var grid = new DeviceGridViewModel(OuiDatabase.Empty);
        grid.Load(_project);

        var diagnostics = new DiagnosticsViewModel(
            new ImmediateDispatcher(),
            () => _project,
            grid,
            pinger,
            new PingSweepOptions { Timeout = TimeSpan.FromMilliseconds(50), PauseBetween = TimeSpan.Zero },
            healthReader: adapter is null
                ? null
                : new DeviceHealthReader { Port = adapter.Port, ConnectTimeout = TimeSpan.FromSeconds(2) });

        return (diagnostics, grid);
    }

    private static DeviceRowViewModel Planned(DeviceGridViewModel grid, string mac, string ip)
    {
        grid.AddDeviceCommand.Execute(null);
        DeviceRowViewModel row = grid.Rows[^1];

        row.MacText = mac;
        row.MaskText = "255.255.255.0";
        row.IpText = ip;

        return row;
    }
}
