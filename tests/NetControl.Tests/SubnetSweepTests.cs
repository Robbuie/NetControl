using System.Net;
using NetControl.Core;
using NetControl.Core.Discovery;
using NetControl.Core.Interfaces;
using NetControl.Core.Persistence;
using NetControl.Core.Reachability;
using Xunit;

namespace NetControl.Tests;

/// <summary>
/// The subnet sweep: what it covers, what it refuses before sending anything, and how it joins the
/// pings to the ARP cache. Every test uses <see cref="FakePinger"/> and <see cref="FakeArpLookup"/>,
/// so no test here puts an echo request on anything.
/// </summary>
public class SubnetSweepTests
{
    private static readonly MacAddress DriveMac = MacAddress.Parse("00:1D:9C:C7:B0:71");
    private static readonly MacAddress SilentMac = MacAddress.Parse("00:1D:9C:C7:B0:72");

    private static readonly SubnetSweepOptions Quick = new()
    {
        Timeout = TimeSpan.FromMilliseconds(50),
        PauseBetween = TimeSpan.Zero,
        ArpSettle = TimeSpan.Zero,
        MaxInFlight = 32,
    };

    private static SubnetSweep Sweep(FakePinger pinger, IArpLookup? arp = null, SubnetSweepOptions? options = null) =>
        new(pinger, arp ?? FakeArpLookup.Empty(), new FakeOuiLookup(), options ?? Quick);

    /// <summary>Every host address on a /24, except the network, the broadcast and this PC's own.</summary>
    [Fact]
    public async Task PingsEveryHostAddressExceptItsOwn()
    {
        var pinger = new FakePinger { Delay = TimeSpan.Zero };

        SubnetSweepResult result = await Sweep(pinger).SweepAsync(Nics.Healthy());

        Assert.Equal(253, result.AddressesPinged);
        Assert.Equal(253, pinger.Asked.Count);
        Assert.DoesNotContain(IPAddress.Parse("192.168.1.10"), pinger.Asked);
        Assert.DoesNotContain(IPAddress.Parse("192.168.1.0"), pinger.Asked);
        Assert.DoesNotContain(IPAddress.Parse("192.168.1.255"), pinger.Asked);
        Assert.Contains(IPAddress.Parse("192.168.1.1"), pinger.Asked);
        Assert.Contains(IPAddress.Parse("192.168.1.254"), pinger.Asked);
    }

    /// <summary>
    /// The whole point of reading the cache: a device set to ignore ping still answers ARP, so it is
    /// found, with its MAC, and said to be silent rather than absent.
    /// </summary>
    [Fact]
    public async Task FindsADeviceThatIgnoresPingThroughTheArpCache()
    {
        var pinger = new FakePinger { Delay = TimeSpan.Zero }.Answers("192.168.1.51", PingStatus.Replied);
        var arp = new FakeArpLookup(
            new ArpEntry(IPAddress.Parse("192.168.1.51"), DriveMac, 12, ArpEntryType.Dynamic),
            new ArpEntry(IPAddress.Parse("192.168.1.52"), SilentMac, 12, ArpEntryType.Dynamic));

        SubnetSweepResult result = await Sweep(pinger, arp).SweepAsync(Nics.Healthy());

        Assert.Equal(2, result.Hosts.Count);

        SweptHost drive = result.Hosts[0];
        Assert.Equal(IPAddress.Parse("192.168.1.51"), drive.Address);
        Assert.True(drive.AnsweredPing);
        Assert.Equal(DriveMac, drive.Mac);
        Assert.Equal("Rockwell Automation", drive.Vendor);
        Assert.True(drive.CanBePlanned);

        SweptHost silent = result.Hosts[1];
        Assert.False(silent.AnsweredPing);
        Assert.True(silent.InArp);
        Assert.Equal(SilentMac, silent.Mac);
        Assert.Equal("ARP only", silent.HowFound);
        Assert.True(silent.CanBePlanned);

        Assert.Equal(1, result.AnsweredPing);
        Assert.Equal(1, result.ArpOnly);
        Assert.Contains("1 answered ARP only", result.Summary);
    }

    /// <summary>Silence with no ARP entry is not a host, and it does not fill the list.</summary>
    [Fact]
    public async Task LeavesOutAnAddressNothingAnsweredAt()
    {
        var pinger = new FakePinger { Delay = TimeSpan.Zero }.Answers("192.168.1.20", PingStatus.Replied);

        SubnetSweepResult result = await Sweep(pinger).SweepAsync(Nics.Healthy());

        SweptHost only = Assert.Single(result.Hosts);
        Assert.Equal(IPAddress.Parse("192.168.1.20"), only.Address);
        Assert.Equal(MacSource.NotInArp, only.MacSource);
        Assert.False(only.CanBePlanned);
        Assert.NotNull(only.PlanningObstacle);
    }

    /// <summary>
    /// The same rule the identity scan keeps: a MAC another adapter learned is very likely another
    /// device on another segment, and is not taken.
    /// </summary>
    [Fact]
    public async Task DoesNotTakeAMacLearnedOnAnotherAdapter()
    {
        var pinger = new FakePinger { Delay = TimeSpan.Zero }.Answers("192.168.1.30", PingStatus.Replied);
        var arp = new FakeArpLookup(new ArpEntry(IPAddress.Parse("192.168.1.30"), DriveMac, 7, ArpEntryType.Dynamic));

        SubnetSweepResult result = await Sweep(pinger, arp).SweepAsync(Nics.Healthy());

        SweptHost host = Assert.Single(result.Hosts);
        Assert.True(host.Mac.IsEmpty);
        Assert.Equal(MacSource.ArpOnAnotherAdapter, host.MacSource);
        Assert.False(host.CanBePlanned);
    }

    /// <summary>An ARP entry that is not on this subnet - another segment, a static multicast entry - is not a host here.</summary>
    [Fact]
    public async Task IgnoresArpEntriesOffTheSubnetAndTheBroadcast()
    {
        var pinger = new FakePinger { Delay = TimeSpan.Zero };
        var arp = new FakeArpLookup(
            new ArpEntry(IPAddress.Parse("10.0.0.5"), DriveMac, 12, ArpEntryType.Dynamic),
            new ArpEntry(IPAddress.Parse("192.168.1.255"), MacAddress.Parse("FF:FF:FF:FF:FF:FF"), 12, ArpEntryType.Static),
            new ArpEntry(IPAddress.Parse("192.168.1.40"), SilentMac, 12, ArpEntryType.Invalid));

        SubnetSweepResult result = await Sweep(pinger, arp).SweepAsync(Nics.Healthy());

        Assert.Empty(result.Hosts);
        Assert.Equal(EventSeverity.Warn, result.Severity);
        Assert.Contains("Nothing answered at all", result.Summary);
    }

    /// <summary>A machine whose ARP table cannot be read still sweeps by ping, and says what it is missing.</summary>
    [Fact]
    public async Task SaysSoWhenTheArpTableCannotBeRead()
    {
        var pinger = new FakePinger { Delay = TimeSpan.Zero }.Answers("192.168.1.20", PingStatus.Replied);

        SubnetSweepResult result = await Sweep(pinger, FakeArpLookup.Unavailable()).SweepAsync(Nics.Healthy());

        SweptHost host = Assert.Single(result.Hosts);
        Assert.Equal(MacSource.NotChecked, host.MacSource);
        Assert.False(result.ArpChecked);
        Assert.Contains("ARP table could not be read", result.Summary);
    }

    /// <summary>Hosts come back in address order as a number, so .2 is above .100.</summary>
    [Fact]
    public async Task ListsHostsInNumericAddressOrder()
    {
        var pinger = new FakePinger { Delay = TimeSpan.Zero }
            .Answers("192.168.1.100", PingStatus.Replied)
            .Answers("192.168.1.2", PingStatus.Replied)
            .Answers("192.168.1.20", PingStatus.Replied);

        SubnetSweepResult result = await Sweep(pinger).SweepAsync(Nics.Healthy());

        Assert.Equal(
            ["192.168.1.2", "192.168.1.20", "192.168.1.100"],
            result.Hosts.Select(h => h.Address.ToString()).ToArray());
    }

    /// <summary>Gentle: never more in flight than the option allows.</summary>
    [Fact]
    public async Task KeepsToTheConcurrencyLimit()
    {
        var pinger = new FakePinger { Delay = TimeSpan.FromMilliseconds(2) };

        await Sweep(pinger, options: Quick with { MaxInFlight = 4 }).SweepAsync(Nics.Healthy(mask: "255.255.255.224"));

        Assert.True(pinger.MaxConcurrent <= 4, $"{pinger.MaxConcurrent} pings were in flight at once.");
    }

    /// <summary>A subnet too wide to sweep is refused before anything is sent, with the reason.</summary>
    [Fact]
    public async Task RefusesASubnetWiderThanTheLimitWithNothingSent()
    {
        var pinger = new FakePinger();
        SubnetSweep sweep = Sweep(pinger);

        ReachabilityException refused = await Assert.ThrowsAsync<ReachabilityException>(
            () => sweep.SweepAsync(Nics.Healthy(address: "10.1.2.3", mask: "255.255.0.0")));

        Assert.Contains("65,533", refused.Message);
        Assert.Empty(pinger.Asked);
    }

    [Fact]
    public async Task RefusesAnAdapterThatCannotSourceASweep()
    {
        var pinger = new FakePinger();
        SubnetSweep sweep = Sweep(pinger);

        await Assert.ThrowsAsync<ReachabilityException>(() => sweep.SweepAsync(Nics.Down()));
        await Assert.ThrowsAsync<ReachabilityException>(() => sweep.SweepAsync(Nics.Apipa()));
        await Assert.ThrowsAsync<ReachabilityException>(() => sweep.SweepAsync(Nics.Healthy(mask: "255.255.255.255")));

        Assert.Empty(pinger.Asked);
    }

    /// <summary>The caption under the checkbox comes from here, so it must agree with what a sweep would do.</summary>
    [Fact]
    public void PlansWithoutSending()
    {
        var pinger = new FakePinger();
        SubnetSweep sweep = Sweep(pinger);

        Assert.True(sweep.TryPlan(Nics.Healthy(), out Ipv4Subnet subnet, out int addresses, out string? problem));
        Assert.Equal("192.168.1.0/24", subnet.ToString());
        Assert.Equal(253, addresses);
        Assert.Null(problem);

        Assert.False(sweep.TryPlan(null, out _, out _, out problem));
        Assert.NotNull(problem);
        Assert.Empty(pinger.Asked);
    }

    /// <summary>Progress arrives once per address, so the status line can count.</summary>
    [Fact]
    public async Task ReportsProgressPerAddress()
    {
        var pinger = new FakePinger { Delay = TimeSpan.Zero };
        SubnetSweep sweep = Sweep(pinger);
        int reports = 0;
        sweep.Progress += (_, _) => Interlocked.Increment(ref reports);

        await sweep.SweepAsync(Nics.Healthy(mask: "255.255.255.240"));

        // A /28 has 14 hosts; one of them is this PC.
        Assert.Equal(13, reports);
    }
}
