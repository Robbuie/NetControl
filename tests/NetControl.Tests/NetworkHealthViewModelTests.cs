using System.Net;
using NetControl.App.Diagnostics;
using NetControl.App.Serving;
using NetControl.App.ViewModels;
using NetControl.Core;
using NetControl.Core.Analysis;
using NetControl.Core.Dhcp;
using NetControl.Core.Interfaces;
using NetControl.Core.Oui;
using NetControl.Core.Persistence;
using NetControl.Core.Reachability;
using NetControl.Core.Safety;
using Xunit;

namespace NetControl.Tests;

/// <summary>
/// The Network health tab and the safety controls, through the view models. No socket is opened:
/// the pinger is a fake, the ARP table is a fake, and every guard is the test's own.
/// </summary>
public sealed class NetworkHealthViewModelTests : IDisposable
{
    private readonly ProjectStore _project = ProjectStore.CreateInMemory("Test");
    private readonly FakeNicInventory _nics = new();
    private readonly StaticMapPolicy _policy = new();
    private readonly NetworkGuard _guard = new();
    private readonly MutableArp _arp = new();
    private readonly FakePinger _pinger = new() { Delay = TimeSpan.Zero };
    private readonly List<SafetySettings> _saved = [];
    private readonly MainViewModel _viewModel;

    public NetworkHealthViewModelTests()
    {
        _viewModel = new MainViewModel(
            new ImmediateDispatcher(),
            _nics,
            new StubPreflight(),
            new ServerController(_nics, _policy),
            _policy,
            OuiDatabase.Empty,
            _project,
            new TestTimeProvider(Events.At),
            pinger: _pinger,
            arp: _arp,
            guard: _guard,
            saveSafety: _saved.Add);

        _viewModel.InterfaceBar.ApplyInventory([Nics.Healthy()]);
        _viewModel.InterfaceBar.SelectedAdapter = _viewModel.InterfaceBar.Adapters[0];
    }

    public void Dispose() => _viewModel.Dispose();

    [Fact]
    public void ListenOnlyIsTheGuardsAndIsSavedAndRecorded()
    {
        int before = _project.Events.Count;

        _viewModel.IsListenOnly = true;

        Assert.True(_guard.ListenOnly);
        Assert.True(Assert.Single(_saved).ListenOnly);
        Assert.Equal(before + 1, _project.Events.Count);
        Assert.Contains("LISTEN ONLY", _viewModel.NetworkActivityText, StringComparison.Ordinal);
    }

    [Fact]
    public void ServeCannotStartInListenOnlyMode()
    {
        _viewModel.IsServeArmed = true;
        Assert.True(_viewModel.StartServeCommand.CanExecute(null));

        _viewModel.IsListenOnly = true;

        Assert.False(_viewModel.StartServeCommand.CanExecute(null));
        Assert.True(_viewModel.StartWatchCommand.CanExecute(null));
    }

    [Fact]
    public void TheSendCapTextIsParsedAndAJunkValueGivesTheDefault()
    {
        _viewModel.MaxPacketsPerSecondText = "50";
        Assert.Equal(50, _guard.MaxPacketsPerSecond);

        _viewModel.MaxPacketsPerSecondText = "lots";
        Assert.Equal(NetworkGuard.DefaultMaxPacketsPerSecond, _guard.MaxPacketsPerSecond);
    }

    [Fact]
    public void AnArpEntryThatFlipsToAnotherMacIsListedAndRecorded()
    {
        IPAddress address = IPAddress.Parse("192.168.1.51");
        _arp.Entries = [new ArpEntry(address, MacAddress.Parse("00:00:BC:11:22:33"), 12, ArpEntryType.Dynamic)];
        _viewModel.Health.ReadArpOnce();
        Assert.Empty(_viewModel.Health.Conflicts);

        _arp.Entries = [new ArpEntry(address, MacAddress.Parse("00:0E:8C:44:55:66"), 12, ArpEntryType.Dynamic)];
        _viewModel.Health.ReadArpOnce();

        ConflictRowViewModel row = Assert.Single(_viewModel.Health.Conflicts);
        Assert.Equal("192.168.1.51", row.AddressText);
        Assert.Contains(_project.Events.All(), e => e.Target == "192.168.1.51" && e.Message.Contains("192.168.1.51", StringComparison.Ordinal));
    }

    [Fact]
    public void ThePingMonitorRefusesABroadcastAndNamesAPlannedDevice()
    {
        _viewModel.Plan.AddDeviceCommand.Execute(null);
        DeviceRowViewModel row = _viewModel.Plan.Rows[^1];
        row.IpText = "192.168.1.51";
        row.Role = "Conveyor drive";

        _viewModel.Health.PingTargetText = "192.168.1.255";
        _viewModel.Health.AddTargetCommand.Execute(null);
        Assert.NotNull(_viewModel.Health.ErrorMessage);
        Assert.Empty(_viewModel.Health.Targets);

        _viewModel.Health.PingTargetText = "192.168.1.51";
        _viewModel.Health.AddTargetCommand.Execute(null);

        PingTargetRowViewModel target = Assert.Single(_viewModel.Health.Targets);
        Assert.Equal("Conveyor drive", target.Name);
        Assert.Equal("waiting", target.State);
    }

    [Fact]
    public async Task ARoundUpdatesTheRowInPlace()
    {
        _pinger.Answers("192.168.1.51", PingStatus.Replied, PingStatus.TimedOut);
        _viewModel.Health.PingTargetText = "192.168.1.51";
        _viewModel.Health.AddTargetCommand.Execute(null);
        PingTargetRowViewModel row = Assert.Single(_viewModel.Health.Targets);

        await _viewModel.Health.Monitor.RoundAsync();
        await _viewModel.Health.Monitor.RoundAsync();

        Assert.Same(row, Assert.Single(_viewModel.Health.Targets));
        Assert.Equal(2, row.Stats.Sent);
        Assert.Equal("dropping", row.State);
        Assert.NotEmpty(row.Sparkline);
    }

    [Fact]
    public void TheMonitorWillNotStartInListenOnlyMode()
    {
        _viewModel.Health.PingTargetText = "192.168.1.51";
        _viewModel.Health.AddTargetCommand.Execute(null);
        _guard.ListenOnly = true;

        _viewModel.Health.StartMonitorCommand.Execute(null);

        Assert.False(_viewModel.Health.IsMonitoring);
        Assert.Contains("Listen-only", _viewModel.Health.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void ArpFramesHeardOnThePassiveTabFeedTheConflictWatchAndTheTrafficCount()
    {
        IPAddress address = IPAddress.Parse("192.168.1.51");

        _viewModel.Passive.Observe(ArpReply(MacAddress.Parse("00:00:BC:11:22:33"), address), Events.At);
        _viewModel.Passive.Observe(ArpReply(MacAddress.Parse("00:0E:8C:44:55:66"), address), Events.At);

        Assert.Equal(address, Assert.Single(_viewModel.Health.Detector.Conflicts()).Address);
        Assert.Equal(2, _viewModel.Passive.TrafficMonitor.Snapshot().Arp);
    }

    private static byte[] ArpReply(MacAddress from, IPAddress claimed)
    {
        var frame = new byte[42];
        MacAddress.Parse("FF:FF:FF:FF:FF:FF").CopyTo(frame);
        from.CopyTo(frame.AsSpan(6));
        frame[12] = 0x08;
        frame[13] = 0x06;
        byte[] arp = [0, 1, 0x08, 0x00, 6, 4, 0, 2];
        arp.CopyTo(frame, 14);
        from.CopyTo(frame.AsSpan(22));
        claimed.GetAddressBytes().CopyTo(frame, 28);
        IPAddress.Parse("192.168.1.10").GetAddressBytes().CopyTo(frame, 38);
        return frame;
    }

    private sealed class MutableArp : IArpLookup
    {
        public IReadOnlyList<ArpEntry> Entries { get; set; } = [];

        public bool IsSupported => true;

        public IReadOnlyList<ArpEntry> Snapshot() => Entries;
    }
}
