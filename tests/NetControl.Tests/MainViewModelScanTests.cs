using System.Net;
using NetControl.App.Serving;
using NetControl.App.ViewModels;
using NetControl.Core;
using NetControl.Core.Dhcp;
using NetControl.Core.Discovery;
using NetControl.Core.Interfaces;
using NetControl.Core.Oui;
using NetControl.Core.Persistence;
using NetControl.Core.Reachability;
using Xunit;

namespace NetControl.Tests;

/// <summary>
/// The scan, as the window uses it: what reaches the screen, what reaches the plan, and what
/// reaches the append-only record.
///
/// <para>Nothing here opens a socket - the scanner is faked. The rule being tested is not how a
/// datagram is framed but that a scan which transmitted is accounted for afterwards, and that
/// nothing about a scan can put an address into the plan that the device did not itself report.</para>
/// </summary>
public sealed class MainViewModelScanTests : IDisposable
{
    private static readonly MacAddress DeviceMac = MacAddress.Parse("00:1D:9C:C7:B0:70");
    private static readonly MacAddress SwitchMac = MacAddress.Parse("00:0E:C6:00:00:01");
    private static readonly MacAddress SilentMac = MacAddress.Parse("00:1D:9C:C7:B0:72");

    private readonly ProjectStore _project = ProjectStore.CreateInMemory("Test");
    private readonly StaticMapPolicy _policy = new();
    private readonly FakeNicInventory _nics = new();
    private readonly ServerController _controller;

    public MainViewModelScanTests() => _controller = new ServerController(_nics, _policy);

    [Fact]
    public async Task PutsWhatAnsweredOnTheScreen()
    {
        MainViewModel viewModel = Build(out _);
        SelectAdapter(viewModel);

        await viewModel.ScanCommand.ExecuteAsync(null);

        ScanResultViewModel row = Assert.Single(viewModel.Scan.Rows);
        Assert.Equal("192.168.1.51", row.AddressText);
        Assert.Equal("1734-AENT/B", row.ProductText);
        Assert.Equal("00:1D:9C:C7:B0:70", row.MacText);
        Assert.Equal("Rockwell Automation", row.VendorText);
        Assert.True(row.CanBePlanned);
    }

    /// <summary>
    /// A scan put packets on what may be a plant network. That belongs in the record whether or not
    /// it found anything, and the record is append-only, so this is the whole of the audit trail.
    /// </summary>
    [Fact]
    public async Task RecordsTheScanInTheAppendOnlyLog()
    {
        MainViewModel viewModel = Build(out _);
        SelectAdapter(viewModel);

        await viewModel.ScanCommand.ExecuteAsync(null);

        EventRecord row = Assert.Single(_project.Events.All());
        Assert.Equal(EventCategory.Scan, row.Category);
        Assert.Equal(EventSeverity.Info, row.Severity);
        Assert.Contains("1 device(s) answered", row.Message, StringComparison.Ordinal);
        Assert.Contains("Ethernet", row.Target ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>A scan that found nothing still transmitted, so it is still recorded.</summary>
    [Fact]
    public async Task RecordsAScanThatFoundNothing()
    {
        MainViewModel viewModel = Build(out _, new FakeIdentityScanner());
        SelectAdapter(viewModel);

        await viewModel.ScanCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.Scan.Rows);
        Assert.Equal(EventCategory.Scan, Assert.Single(_project.Events.All()).Category);
    }

    /// <summary>Two devices on one address is a fault on the segment, and it is graded as one.</summary>
    [Fact]
    public async Task RecordsAContestedAddressAsAWarning()
    {
        MainViewModel viewModel = Build(
            out _,
            new FakeIdentityScanner(
                Identities.Reply(serial: 0x00C0FFEE),
                Identities.Reply(serial: 0x0BADF00D, product: "PowerFlex 525")));

        SelectAdapter(viewModel);

        await viewModel.ScanCommand.ExecuteAsync(null);

        Assert.Equal(EventSeverity.Warn, Assert.Single(_project.Events.All()).Severity);
        Assert.All(viewModel.Scan.Rows, row => Assert.True(row.NeedsAttention));
    }

    /// <summary>
    /// The address comes across because the device reported it - it is a fact read off the wire,
    /// not the "lowest free address" suggestion the log-row path refuses to make. The mask comes
    /// from the adapter only because the device answered from inside that adapter's subnet.
    /// </summary>
    [Fact]
    public async Task PlanningAScanResultCarriesTheAddressTheDeviceIsAlreadyHolding()
    {
        MainViewModel viewModel = Build(out _);
        SelectAdapter(viewModel);
        await viewModel.ScanCommand.ExecuteAsync(null);

        viewModel.PlanFromScan(viewModel.Scan.Rows[0]);

        DeviceRowViewModel planned = Assert.Single(viewModel.Plan.Rows);
        Assert.Equal(DeviceMac, planned.Mac);
        Assert.Equal("192.168.1.51", planned.IpText);
        Assert.Equal("255.255.255.0", planned.MaskText);
        Assert.Contains("1734-AENT/B", planned.Notes, StringComparison.Ordinal);
        Assert.True(planned.IsServable);
    }

    /// <summary>
    /// A device on another subnet is reachable through a router and may have any prefix at all.
    /// Guessing one produces a device that comes up, mostly works, and cannot reach half the plant.
    /// </summary>
    [Fact]
    public async Task DoesNotInventAMaskForADeviceOffTheAdaptersSubnet()
    {
        // The ARP entry is at the device's own address, not the adapter's subnet: a device reached
        // through a router is still in the cache, so this test is about the mask and nothing else.
        MainViewModel viewModel = Build(
            out _,
            new FakeIdentityScanner(Identities.Reply("10.4.7.9")),
            FakeArpLookup.Holding("10.4.7.9", DeviceMac));

        SelectAdapter(viewModel);
        await viewModel.ScanCommand.ExecuteAsync(null);

        viewModel.PlanFromScan(viewModel.Scan.Rows[0]);

        DeviceRowViewModel planned = Assert.Single(viewModel.Plan.Rows);
        Assert.Equal("10.4.7.9", planned.IpText);
        Assert.Equal(string.Empty, planned.MaskText);

        // And it says so on the row rather than looking finished: no mask means it will not be served.
        Assert.False(planned.IsServable);
    }

    /// <summary>
    /// The plan is keyed on MAC. A result without one is refused, and refused out loud - with the
    /// same sentence Core wrote, so the scan list and the log cannot explain it differently.
    /// </summary>
    [Fact]
    public async Task RefusesToPlanADeviceWhoseMacCouldNotBeResolved()
    {
        MainViewModel viewModel = Build(out _, arp: FakeArpLookup.Empty());
        SelectAdapter(viewModel);
        await viewModel.ScanCommand.ExecuteAsync(null);

        viewModel.PlanFromScan(viewModel.Scan.Rows[0]);

        Assert.Empty(viewModel.Plan.Rows);
        Assert.Contains(
            viewModel.Log.Entries,
            entry => entry.Message.Contains("cannot go into the plan", StringComparison.Ordinal));
    }

    /// <summary>Planning the same device twice selects the row it is already on and changes nothing.</summary>
    [Fact]
    public async Task PlanningTheSameScanResultTwiceDoesNotAddASecondRow()
    {
        MainViewModel viewModel = Build(out _);
        SelectAdapter(viewModel);
        await viewModel.ScanCommand.ExecuteAsync(null);

        viewModel.PlanFromScan(viewModel.Scan.Rows[0]);
        viewModel.Plan.Rows[0].Role = "Conveyor 3 drive";
        viewModel.PlanFromScan(viewModel.Scan.Rows[0]);

        DeviceRowViewModel planned = Assert.Single(viewModel.Plan.Rows);
        Assert.Equal("Conveyor 3 drive", planned.Role);
        Assert.True(viewModel.Scan.Rows[0].IsAlreadyPlanned);
    }

    /// <summary>
    /// The sweep is what turns Scan into "what is on this network": everything that answered ping
    /// or ARP gets a row, and an EtherNet/IP device that answered both is one row carrying both.
    /// </summary>
    [Fact]
    public async Task ASweepAddsEverythingElseOnTheSubnetAndJoinsByAddress()
    {
        var pinger = new FakePinger { Delay = TimeSpan.Zero }
            .Answers("192.168.1.51", PingStatus.Replied)
            .Answers("192.168.1.1", PingStatus.Replied);
        var arp = new FakeArpLookup(
            new ArpEntry(IPAddress.Parse("192.168.1.51"), DeviceMac, 12, ArpEntryType.Dynamic),
            new ArpEntry(IPAddress.Parse("192.168.1.1"), SwitchMac, 12, ArpEntryType.Dynamic),
            new ArpEntry(IPAddress.Parse("192.168.1.60"), SilentMac, 12, ArpEntryType.Dynamic));

        MainViewModel viewModel = Build(out _, arp: arp, pinger: pinger);
        SelectAdapter(viewModel);

        await viewModel.ScanCommand.ExecuteAsync(null);

        Assert.Equal(
            ["192.168.1.1", "192.168.1.51", "192.168.1.60"],
            viewModel.Scan.Rows.Select(r => r.AddressText).ToArray());

        ScanResultViewModel gateway = viewModel.Scan.Rows[0];
        Assert.False(gateway.IsEtherNetIp);
        Assert.Equal(string.Empty, gateway.ProductText);
        Assert.Equal("00:0E:C6:00:00:01", gateway.MacText);
        Assert.Equal("2 ms", gateway.ResponseText);
        Assert.True(gateway.CanBePlanned);

        ScanResultViewModel adapter = viewModel.Scan.Rows[1];
        Assert.True(adapter.IsEtherNetIp);
        Assert.Equal("1734-AENT/B", adapter.ProductText);
        Assert.Equal("2 ms", adapter.ResponseText);

        Assert.Equal("ARP only", viewModel.Scan.Rows[2].ResponseText);
        Assert.Contains("answered ARP only", viewModel.Scan.Status, StringComparison.Ordinal);
    }

    /// <summary>Each half transmitted, so each half is in the record - and the sweep row says how much it sent.</summary>
    [Fact]
    public async Task RecordsTheSweepAsItsOwnScanRow()
    {
        var pinger = new FakePinger { Delay = TimeSpan.Zero }.Answers("192.168.1.51", PingStatus.Replied);

        MainViewModel viewModel = Build(out _, pinger: pinger);
        SelectAdapter(viewModel);

        await viewModel.ScanCommand.ExecuteAsync(null);

        IReadOnlyList<EventRecord> rows = _project.Events.All();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal(EventCategory.Scan, row.Category));
        Assert.Contains("Swept 253 address(es) on 192.168.1.0/24", rows[1].Message, StringComparison.Ordinal);
        Assert.Contains("\"pinged\":253", rows[1].Detail ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>Unticked, Scan is the one broadcast it always was and not a single echo request goes out.</summary>
    [Fact]
    public async Task SendsNoPingsWhenTheSweepIsUnticked()
    {
        var pinger = new FakePinger { Delay = TimeSpan.Zero };

        MainViewModel viewModel = Build(out FakeIdentityScanner scanner, pinger: pinger);
        SelectAdapter(viewModel);
        viewModel.Scan.SweepSubnet = false;

        await viewModel.ScanCommand.ExecuteAsync(null);

        Assert.Empty(pinger.Asked);
        Assert.Equal(1, scanner.Scans);
        Assert.Single(viewModel.Scan.Rows);
        Assert.Single(_project.Events.All());
    }

    /// <summary>
    /// A subnet too wide to sweep is said so on the checkbox before anybody presses anything, and
    /// Scan still does its identity half rather than refusing outright.
    /// </summary>
    [Fact]
    public async Task AWideSubnetDisablesTheSweepButNotTheScan()
    {
        var pinger = new FakePinger { Delay = TimeSpan.Zero };

        MainViewModel viewModel = Build(out FakeIdentityScanner scanner, pinger: pinger);
        viewModel.InterfaceBar.ApplyInventory([Nics.Healthy(address: "10.20.1.5", mask: "255.255.0.0")]);
        viewModel.InterfaceBar.SelectedAdapter = viewModel.InterfaceBar.Adapters[0];

        Assert.False(viewModel.Scan.CanSweep);
        Assert.Contains("65,533", viewModel.Scan.SweepTooltip ?? string.Empty, StringComparison.Ordinal);

        await viewModel.ScanCommand.ExecuteAsync(null);

        Assert.Equal(1, scanner.Scans);
        Assert.Empty(pinger.Asked);
    }

    [Fact]
    public void TheSweepCaptionNamesTheSubnetAndTheCount()
    {
        MainViewModel viewModel = Build(out _, pinger: new FakePinger());
        SelectAdapter(viewModel);

        Assert.True(viewModel.Scan.CanSweep);
        Assert.Contains("192.168.1.0/24", viewModel.Scan.SweepCaption, StringComparison.Ordinal);
        Assert.Contains("253 addresses", viewModel.Scan.SweepCaption, StringComparison.Ordinal);
    }

    /// <summary>
    /// Something only the sweep found can still be planned: the address is the one it answered on,
    /// the MAC came out of this adapter's ARP cache, and the mask is the adapter's because the sweep
    /// only ever covers the adapter's own subnet.
    /// </summary>
    [Fact]
    public async Task PlansADeviceOnlyTheSweepFound()
    {
        var pinger = new FakePinger { Delay = TimeSpan.Zero }.Answers("192.168.1.70", PingStatus.Replied);
        var arp = new FakeArpLookup(
            new ArpEntry(IPAddress.Parse("192.168.1.70"), SilentMac, 12, ArpEntryType.Dynamic));

        MainViewModel viewModel = Build(out _, new FakeIdentityScanner(), arp, pinger);
        SelectAdapter(viewModel);

        await viewModel.ScanCommand.ExecuteAsync(null);
        viewModel.PlanFromScan(Assert.Single(viewModel.Scan.Rows));

        DeviceRowViewModel row = Assert.Single(viewModel.Plan.Rows);
        Assert.Equal(SilentMac, row.Mac);
        Assert.Equal("192.168.1.70", row.IpText);
        Assert.Equal("255.255.255.0", row.MaskText);
        Assert.StartsWith("Found by subnet sweep", row.Notes, StringComparison.Ordinal);
        Assert.True(viewModel.Scan.Rows[0].IsAlreadyPlanned);
    }

    /// <summary>
    /// A scan transmits, so it needs an adapter that can source one - chosen deliberately, exactly
    /// like serve mode. There is no "scan on whatever adapter Windows would have picked".
    /// </summary>
    [Fact]
    public void ScanNeedsAnAdapterThatCanSourceOne()
    {
        MainViewModel viewModel = Build(out FakeIdentityScanner scanner);

        Assert.False(viewModel.ScanCommand.CanExecute(null));

        viewModel.InterfaceBar.ApplyInventory([Nics.Apipa()]);
        viewModel.InterfaceBar.SelectedAdapter = viewModel.InterfaceBar.Adapters[0];
        Assert.False(viewModel.ScanCommand.CanExecute(null));

        Assert.Equal(0, scanner.Scans);
    }

    /// <summary>A build with no discovery in it has no scan button, rather than one that throws.</summary>
    [Fact]
    public void HasNoScanAtAllWhenTheAppWasBuiltWithoutDiscovery()
    {
        var viewModel = new MainViewModel(
            new ImmediateDispatcher(),
            _nics,
            new StubPreflight(),
            _controller,
            _policy,
            OuiDatabase.Empty,
            _project,
            new TestTimeProvider(Events.At));

        viewModel.InterfaceBar.ApplyInventory([Nics.Healthy()]);
        viewModel.InterfaceBar.SelectedAdapter = viewModel.InterfaceBar.Adapters[0];

        // Not disposed here: this class owns the controller and the project, and disposing the view
        // model would take both with it while later tests in the same instance still need them.
        Assert.False(viewModel.ScanCommand.CanExecute(null));
    }

    public void Dispose()
    {
        _controller.Dispose();
        _project.Dispose();
    }

    private static void SelectAdapter(MainViewModel viewModel)
    {
        viewModel.InterfaceBar.ApplyInventory([Nics.Healthy()]);
        viewModel.InterfaceBar.SelectedAdapter = viewModel.InterfaceBar.Adapters[0];
    }

    private MainViewModel Build(
        out FakeIdentityScanner scanner,
        FakeIdentityScanner? withScanner = null,
        IArpLookup? arp = null,
        FakePinger? pinger = null)
    {
        scanner = withScanner ?? new FakeIdentityScanner(Identities.Reply());

        IArpLookup table = arp ?? FakeArpLookup.Holding("192.168.1.51", DeviceMac);
        var discovery = new DeviceDiscovery(scanner, table, new FakeOuiLookup());

        // No pinger, no sweep - which is how every test above this one sees Scan: the identity
        // request alone, exactly as it was before the sweep existed.
        SubnetSweep? sweep = pinger is null
            ? null
            : new SubnetSweep(
                pinger,
                table,
                new FakeOuiLookup(),
                new SubnetSweepOptions { Timeout = TimeSpan.FromMilliseconds(20), PauseBetween = TimeSpan.Zero, ArpSettle = TimeSpan.Zero, MaxInFlight = 64 });

        return new MainViewModel(
            new ImmediateDispatcher(),
            _nics,
            new StubPreflight(),
            _controller,
            _policy,
            OuiDatabase.Empty,
            _project,
            new TestTimeProvider(Events.At),
            discovery: discovery,
            sweep: sweep);
    }
}
