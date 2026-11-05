using System.Net;
using NetControl.App.AdapterSetup;
using NetControl.App.ViewModels;
using NetControl.Core.AdapterSetup;
using NetControl.Core.Interfaces;
using NetControl.Core.Persistence;
using Xunit;

namespace NetControl.Tests;

/// <summary>
/// Setting the laptop's own adapter: the netsh lines, the check before them, the profiles the plan
/// offers, and the window's run - against a fake that records what it was asked to run and plays the
/// adapter's state back.
/// </summary>
public sealed class AdapterSetupTests : IDisposable
{
    private static readonly IPAddress Mask24 = IPAddress.Parse("255.255.255.0");

    private readonly ProjectStore _project = ProjectStore.CreateInMemory("Test");

    public void Dispose() => _project.Dispose();

    // ------------------------------------------------------------------ netsh lines

    [Fact]
    public void ReplaceIsOneStaticAddressWithNoGatewayAndNoDns()
    {
        IReadOnlyList<string> lines = NetshScript.For(12, AdapterIpMode.Replace, Settings("120.15.106.250"));

        Assert.Equal(
            [
                "interface ipv4 set address name=12 source=static address=120.15.106.250 mask=255.255.255.0 gateway=none",
                "interface ipv4 set dnsservers name=12 source=static address=none register=none validate=no",
            ],
            lines);
    }

    /// <summary>A gateway on the panel adapter gets a high metric, so Wi-Fi's default route still wins.</summary>
    [Fact]
    public void AGatewayComesWithAHighMetricAndDnsInOrder()
    {
        AdapterIpSettings settings = Settings("192.168.1.250") with
        {
            Gateway = IPAddress.Parse("192.168.1.1"),
            Dns = [IPAddress.Parse("10.0.0.53"), IPAddress.Parse("10.0.0.54")],
        };

        IReadOnlyList<string> lines = NetshScript.For(7, AdapterIpMode.Replace, settings);

        Assert.Contains("gateway=192.168.1.1 gwmetric=500", lines[0], StringComparison.Ordinal);
        Assert.Equal("interface ipv4 set dnsservers name=7 source=static address=10.0.0.53 register=none validate=no", lines[1]);
        Assert.Equal("interface ipv4 add dnsservers name=7 address=10.0.0.54 index=2 validate=no", lines[2]);
    }

    [Fact]
    public void AddAndDhcpAreTheirOwnCommands()
    {
        Assert.Equal(
            ["interface ipv4 add address name=3 address=10.1.1.250 mask=255.255.255.0"],
            NetshScript.For(3, AdapterIpMode.AddAddress, Settings("10.1.1.250")));

        Assert.Equal(
            ["interface ipv4 set address name=3 source=dhcp", "interface ipv4 set dnsservers name=3 source=dhcp"],
            NetshScript.For(3, AdapterIpMode.Dhcp, null));
    }

    [Fact]
    public void RestorePutsBackEveryStaticAddressTheGatewayAndDns()
    {
        var snapshot = new AdapterSnapshot
        {
            Index = 5,
            Name = "Ethernet",
            Addresses = [new(IPAddress.Parse("10.20.0.5"), IPAddress.Parse("255.255.0.0")), new(IPAddress.Parse("192.168.1.9"), Mask24)],
            Gateway = IPAddress.Parse("10.20.0.1"),
            Dns = [IPAddress.Parse("10.20.0.2")],
        };

        IReadOnlyList<string> lines = NetshScript.Restore(snapshot);

        Assert.Equal("interface ipv4 set address name=5 source=static address=10.20.0.5 mask=255.255.0.0 gateway=10.20.0.1", lines[0]);
        Assert.Equal("interface ipv4 add address name=5 address=192.168.1.9 mask=255.255.255.0", lines[1]);
        Assert.Contains("address=10.20.0.2", lines[2], StringComparison.Ordinal);

        Assert.Equal(NetshScript.For(5, AdapterIpMode.Dhcp, null), NetshScript.Restore(snapshot with { IsDhcp = true }));
    }

    [Fact]
    public void SnapshotsSurviveJson()
    {
        var snapshot = new AdapterSnapshot
        {
            Index = 5,
            Name = "Ethernet 2",
            IsDhcp = false,
            Addresses = [new(IPAddress.Parse("10.20.0.5"), IPAddress.Parse("255.255.0.0"))],
            Gateway = IPAddress.Parse("10.20.0.1"),
        };

        AdapterSnapshot? back = AdapterSnapshot.FromJson(snapshot.ToJson());

        Assert.NotNull(back);
        Assert.Equal(snapshot.Summary, back.Summary);
        Assert.Null(AdapterSnapshot.FromJson("not json"));
    }

    // ------------------------------------------------------------------ the check

    [Theory]
    [InlineData("192.168.1.0", "network address")]
    [InlineData("192.168.1.255", "broadcast address")]
    [InlineData("192.168.1.51", "planned for a device")]
    public void RefusesAnAddressNoLaptopShouldHave(string ip, string expected)
    {
        AdapterIpCheck check = AdapterIpCheck.Of(
            AdapterIpMode.Replace, Settings(ip), Snapshot(), [], [IPAddress.Parse("192.168.1.51")]);

        Assert.False(check.IsOk);
        Assert.Contains(check.Problems, p => p.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void RefusesAGatewayOffTheSubnetAndWarnsAboutOneOnIt()
    {
        AdapterIpCheck off = AdapterIpCheck.Of(
            AdapterIpMode.Replace, Settings("192.168.1.250") with { Gateway = IPAddress.Parse("10.0.0.1") }, Snapshot(), [], []);
        Assert.Contains(off.Problems, p => p.Contains("not on 192.168.1.0/24", StringComparison.Ordinal));

        AdapterIpCheck on = AdapterIpCheck.Of(
            AdapterIpMode.Replace, Settings("192.168.1.250") with { Gateway = IPAddress.Parse("192.168.1.1") }, Snapshot(), [], []);
        Assert.True(on.IsOk);
        Assert.Contains(on.Warnings, w => w.Contains("default route", StringComparison.Ordinal));
    }

    [Fact]
    public void RefusesAnAddressAnotherAdapterHasAndWarnsAboutASharedSubnet()
    {
        NicInfo wifi = Nics.Healthy(index: 20, address: "192.168.1.77") with { Name = "Wi-Fi" };

        AdapterIpCheck taken = AdapterIpCheck.Of(AdapterIpMode.Replace, Settings("192.168.1.77"), Snapshot(), [wifi], []);
        Assert.Contains(taken.Problems, p => p.Contains("Wi-Fi already has", StringComparison.Ordinal));

        AdapterIpCheck shared = AdapterIpCheck.Of(AdapterIpMode.Replace, Settings("192.168.1.250"), Snapshot(), [wifi], []);
        Assert.True(shared.IsOk);
        Assert.Contains(shared.Warnings, w => w.Contains("Wi-Fi is on 192.168.1.0/24 too", StringComparison.Ordinal));
    }

    /// <summary>Windows will not put a second address beside a DHCP one, so the check says so first.</summary>
    [Fact]
    public void AddIsRefusedOnADhcpAdapter()
    {
        AdapterIpCheck check = AdapterIpCheck.Of(
            AdapterIpMode.AddAddress, Settings("192.168.1.250"), Snapshot() with { IsDhcp = true }, [], []);

        Assert.Contains(check.Problems, p => p.Contains("DHCP", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ plan profiles

    [Fact]
    public void OffersOneProfilePerPlanSubnetSteppingOverPlannedAddresses()
    {
        IReadOnlyList<AdapterProfile> profiles = AdapterProfile.FromPlan(
        [
            Device("120.15.106.21"),
            Device("120.15.106.250"),
            Device("192.168.1.51"),
            Device("10.0.0.5", "255.255.0.0"),
        ]);

        Assert.Equal(3, profiles.Count);
        Assert.Equal("10.0.0.250", profiles[0].Ip);
        Assert.Equal("255.255.0.0", profiles[0].Mask);
        Assert.Equal("120.15.106.249", profiles[1].Ip);
        Assert.StartsWith("Plan: 120.15.106.0/24 (2 devices)", profiles[1].Name, StringComparison.Ordinal);
        Assert.Equal("192.168.1.250", profiles[2].Ip);
        Assert.All(profiles, p => Assert.Equal(string.Empty, p.Gateway));
    }

    [Fact]
    public void SavedProfilesTravelWithTheProject()
    {
        AdapterProfile.Save(_project.Settings, [new AdapterProfile("Shop bench", "192.168.0.250", "255.255.255.0")]);

        AdapterProfile saved = Assert.Single(AdapterProfile.Load(_project.Settings));
        Assert.Equal("Shop bench", saved.Name);
    }

    // ------------------------------------------------------------------ the window

    [Fact]
    public async Task AppliesAfterAConfirmationAndReadsTheAdapterBack()
    {
        var fake = new FakeConfigurator(Snapshot() with { IsDhcp = true, Addresses = [new(IPAddress.Parse("10.9.8.7"), Mask24)] });
        var store = new InMemoryAdapterSnapshotStore();
        AdapterSetupViewModel vm = Window(fake, store);

        vm.Mode = AdapterIpMode.Replace;
        vm.IpText = "192.168.1.250";
        vm.MaskText = "255.255.255.0";
        Assert.Contains("address=192.168.1.250", vm.CommandsText, StringComparison.Ordinal);

        // Unconfirmed: nothing runs.
        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.Empty(fake.Ran);

        string? asked = null;
        vm.Confirm = question => { asked = question; return true; };
        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.Contains("netsh interface ipv4 set address name=12", asked, StringComparison.Ordinal);
        Assert.Single(fake.Ran);
        Assert.False(vm.StatusIsBad, vm.Status);
        Assert.StartsWith("Done, and read back", vm.Status, StringComparison.Ordinal);

        // What it had before is kept for Restore - DHCP, as it was.
        Assert.True(store.Get("Ethernet")?.IsDhcp);
        Assert.True(vm.HasSaved);
        Assert.Contains(_project.Events.All(), e => e.Message.StartsWith("Adapter changed and read back", StringComparison.Ordinal));
    }

    /// <summary>netsh exiting cleanly is not the change: only the adapter holding the address is.</summary>
    [Fact]
    public async Task SaysSoWhenTheAdapterDoesNotShowTheChange()
    {
        var fake = new FakeConfigurator(Snapshot()) { Ignores = true };
        AdapterSetupViewModel vm = Window(fake, new InMemoryAdapterSnapshotStore());
        vm.Confirm = _ => true;
        vm.IpText = "192.168.1.250";

        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.True(vm.StatusIsBad);
        Assert.Contains("does not show the change", vm.Status, StringComparison.Ordinal);
        Assert.Contains(_project.Events.All(), e => e.Severity == EventSeverity.Error);
    }

    [Fact]
    public async Task RestorePutsBackWhatItHadBeforeTheFirstChange()
    {
        AdapterSnapshot office = Snapshot() with { IsDhcp = true, Addresses = [new(IPAddress.Parse("10.9.8.7"), Mask24)] };
        var fake = new FakeConfigurator(office);
        var store = new InMemoryAdapterSnapshotStore();
        AdapterSetupViewModel vm = Window(fake, store);
        vm.Confirm = _ => true;

        vm.IpText = "192.168.1.250";
        await vm.ApplyCommand.ExecuteAsync(null);
        vm.IpText = "192.168.2.250";
        await vm.ApplyCommand.ExecuteAsync(null);

        // The second change did not overwrite the saved office settings.
        Assert.True(store.Get("Ethernet")?.IsDhcp);

        await vm.RestoreCommand.ExecuteAsync(null);

        Assert.Equal(NetshScript.For(12, AdapterIpMode.Dhcp, null), fake.Ran[^1]);
        Assert.True(fake.Current.IsDhcp);
        Assert.Null(store.Get("Ethernet"));
        Assert.False(vm.HasSaved);
    }

    [Fact]
    public void WillNotChangeTheAdapterUnderARunningListener()
    {
        var fake = new FakeConfigurator(Snapshot());
        var vm = new AdapterSetupViewModel(
            Nics.Healthy(), fake, new InMemoryAdapterSnapshotStore(), () => [Nics.Healthy()], _project, () => true);

        vm.IpText = "192.168.1.250";

        Assert.False(vm.ApplyCommand.CanExecute(null));
        Assert.Contains("Stop the listener", vm.ProblemsText, StringComparison.Ordinal);
    }

    [Fact]
    public void PickingAPlanProfileFillsTheFields()
    {
        _project.Devices.Upsert(new DeviceRecord { Mac = NetControl.Core.MacAddress.Empty, PlannedIp = IPAddress.Parse("120.15.106.21"), PlannedMask = Mask24 });
        AdapterSetupViewModel vm = Window(new FakeConfigurator(Snapshot()), new InMemoryAdapterSnapshotStore());

        vm.SelectedProfile = vm.Profiles[0];

        Assert.Equal("120.15.106.250", vm.IpText);
        Assert.Equal(string.Empty, vm.GatewayText);
        Assert.True(vm.ApplyCommand.CanExecute(null));

        vm.HostNumberText = "200";
        Assert.Equal("120.15.106.200", vm.Profiles[0].Ip);
        Assert.Equal("200", _project.Settings.Get(AdapterProfile.HostNumberKey));
    }

    private AdapterSetupViewModel Window(FakeConfigurator fake, IAdapterSnapshotStore store) =>
        new(Nics.Healthy(), fake, store, () => [Nics.Healthy()], _project, () => false)
        {
            ReadbackTimeout = TimeSpan.FromMilliseconds(100),
            ReadbackInterval = TimeSpan.FromMilliseconds(10),
        };

    private static AdapterIpSettings Settings(string ip) => new() { Ip = IPAddress.Parse(ip), Mask = Mask24 };

    private static AdapterSnapshot Snapshot() => new()
    {
        Index = 12,
        Name = "Ethernet",
        Addresses = [new(IPAddress.Parse("192.168.1.10"), Mask24)],
    };

    private static DeviceRecord Device(string ip, string mask = "255.255.255.0") =>
        new() { Mac = NetControl.Core.MacAddress.Empty, PlannedIp = IPAddress.Parse(ip), PlannedMask = IPAddress.Parse(mask) };

    /// <summary>Records each run and plays the change into the adapter it reports, unless told to ignore it.</summary>
    private sealed class FakeConfigurator(AdapterSnapshot start) : IAdapterConfigurator
    {
        public AdapterSnapshot Current { get; private set; } = start;

        public List<IReadOnlyList<string>> Ran { get; } = [];

        public bool Ignores { get; init; }

        public AdapterSnapshot? Read(int index) => Current;

        public Task<NetshRun> RunAsync(IReadOnlyList<string> lines, CancellationToken cancellationToken = default)
        {
            Ran.Add(lines);

            if (!Ignores)
            {
                string first = lines[0];
                if (first.Contains("source=dhcp", StringComparison.Ordinal))
                {
                    Current = Current with { IsDhcp = true };
                }
                else
                {
                    string ip = Between(first, "address=", " ");
                    string mask = Between(first, "mask=", " ");
                    var address = new AdapterAddress(IPAddress.Parse(ip), IPAddress.Parse(mask));
                    Current = first.Contains(" add address", StringComparison.Ordinal)
                        ? Current with { Addresses = [.. Current.Addresses, address] }
                        : Current with { IsDhcp = false, Addresses = [address] };
                }
            }

            return Task.FromResult(new NetshRun(true, false, 0, string.Empty));
        }

        private static string Between(string text, string start, string end)
        {
            int from = text.IndexOf(start, StringComparison.Ordinal) + start.Length;
            int to = text.IndexOf(end, from, StringComparison.Ordinal);
            return to < 0 ? text[from..] : text[from..to];
        }
    }
}
