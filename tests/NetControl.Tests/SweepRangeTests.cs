using System.Net;
using NetControl.Core;
using NetControl.Core.Discovery;
using NetControl.Core.Interfaces;
using NetControl.Core.Reachability;
using Xunit;

namespace NetControl.Tests;

/// <summary>
/// Scanning more than one subnet: the typed ranges, an adapter with two addresses, and what a sweep
/// through a router can and cannot learn. Every test uses <see cref="FakePinger"/>,
/// <see cref="FakeArpLookup"/> and <see cref="FakeIdentityScanner"/>, so nothing is sent anywhere.
/// </summary>
public class SweepRangeTests
{
    private static readonly MacAddress DriveMac = MacAddress.Parse("00:1D:9C:C7:B0:71");

    private static readonly SubnetSweepOptions Quick = new()
    {
        Timeout = TimeSpan.FromMilliseconds(20),
        PauseBetween = TimeSpan.Zero,
        ArpSettle = TimeSpan.Zero,
        MaxInFlight = 64,
    };

    [Theory]
    [InlineData("10.20.5.0/24", "10.20.5.0/24", "10.20.5.1", "10.20.5.254", 254)]
    [InlineData("10.20.6.10-10.20.6.50", "10.20.6.10-10.20.6.50", "10.20.6.10", "10.20.6.50", 41)]
    [InlineData("10.20.6.10-50", "10.20.6.10-10.20.6.50", "10.20.6.10", "10.20.6.50", 41)]
    [InlineData(" 10.20.7.4 ", "10.20.7.4", "10.20.7.4", "10.20.7.4", 1)]
    [InlineData("10.20.5.77/24", "10.20.5.0/24", "10.20.5.1", "10.20.5.254", 254)]
    public void ReadsEachFormSomebodyMightType(string text, string tidy, string first, string last, int count)
    {
        Assert.True(SweepRange.TryParse(text, 1024, out SweepRange? range, out string? problem), problem);

        Assert.Equal(tidy, range!.Text);
        Assert.Equal(IPAddress.Parse(first), range.First);
        Assert.Equal(IPAddress.Parse(last), range.Last);
        Assert.Equal(count, range.Count);
    }

    /// <summary>
    /// A half-typed address is refused, not read the way IPAddress.Parse reads it - "10.20.5" is not
    /// 10.20.0.5, and sweeping the wrong subnet of somebody's plant is not a harmless typo.
    /// </summary>
    [Theory]
    [InlineData("10.20.5")]
    [InlineData("10.20.5.0/33")]
    [InlineData("10.20.5.0/")]
    [InlineData("10.20.6.50-10")]
    [InlineData("10.20.6.10-x")]
    [InlineData("conveyor")]
    [InlineData("224.0.0.1-224.0.0.9")]
    [InlineData("10.0.0.0/8")]
    public void RefusesWhatIsNotARangeOfHosts(string text)
    {
        Assert.False(SweepRange.TryParse(text, 1024, out SweepRange? range, out string? problem));
        Assert.Null(range);
        Assert.Contains(text.Trim(), problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadsAListAndReportsEveryBadPieceAtOnce()
    {
        Assert.True(SweepRange.TryParseList("10.20.5.0/24, 10.20.6.10-50;10.20.7.4\n10.20.5.0/24", 1024,
            out IReadOnlyList<SweepRange> ranges, out _));
        Assert.Equal(3, ranges.Count);

        Assert.False(SweepRange.TryParseList("10.20.5.0/24, 10.20.5, banana", 1024, out _, out string? problem));
        Assert.Contains("'10.20.5'", problem!, StringComparison.Ordinal);
        Assert.Contains("'banana'", problem!, StringComparison.Ordinal);

        Assert.True(SweepRange.TryParseList("   ", 1024, out ranges, out problem));
        Assert.Empty(ranges);
        Assert.Null(problem);
    }

    /// <summary>A second address on the same adapter is a second subnet to scan, from that address.</summary>
    [Fact]
    public void AnAdapterWithTwoAddressesIsScannedOncePerSubnet()
    {
        NicInfo nic = Nics.Healthy() with
        {
            ExtraAddresses = new NicAddressList(
            [
                Subnet("192.168.0.10", "255.255.255.0"),
                Subnet("192.168.1.99", "255.255.255.0"),   // same subnet as the first: not twice
                Subnet("169.254.7.7", "255.255.0.0"),      // APIPA: not a subnet anybody commissions on
            ]),
        };

        IReadOnlyList<NicInfo> parts = nic.PerAddress();

        Assert.Equal(2, parts.Count);
        Assert.Equal(IPAddress.Parse("192.168.1.10"), parts[0].IPv4);
        Assert.Equal(IPAddress.Parse("192.168.0.10"), parts[1].IPv4);
        Assert.All(parts, p => Assert.Equal(nic.Index, p.Index));
        Assert.True(nic.Owns(IPAddress.Parse("192.168.0.10")));
    }

    /// <summary>
    /// The inventory decides whether adapters changed by comparing records. A list compared by
    /// reference would make every poll look like a change.
    /// </summary>
    [Fact]
    public void TwoReadsOfTheSameAddressesAreEqual()
    {
        NicInfo a = Nics.Healthy() with { ExtraAddresses = new NicAddressList([Subnet("192.168.0.10", "255.255.255.0")]) };
        NicInfo b = Nics.Healthy() with { ExtraAddresses = new NicAddressList([Subnet("192.168.0.10", "255.255.255.0")]) };

        Assert.Equal(a, b);
        Assert.NotEqual(a, Nics.Healthy());
    }

    /// <summary>
    /// Behind a router: what answered ping is found, nothing else, and none of it has a MAC - which
    /// the row says, rather than suggesting the device is missing from a cache.
    /// </summary>
    [Fact]
    public async Task ARoutedRangeFindsWhatAnswersPingWithNoMac()
    {
        var pinger = new FakePinger { Delay = TimeSpan.Zero }.Answers("10.20.5.40", PingStatus.Replied);
        var sweep = new SubnetSweep(pinger, FakeArpLookup.Empty(), new FakeOuiLookup(), Quick);
        SweepRange range = Range("10.20.5.32-47");

        SubnetSweepResult result = await sweep.SweepRangeAsync(Nics.Healthy(), range);

        Assert.True(result.IsRouted);
        Assert.Equal(16, result.AddressesPinged);
        Assert.Equal(16, pinger.Asked.Count);

        SweptHost host = Assert.Single(result.Hosts);
        Assert.Equal(MacSource.Routed, host.MacSource);
        Assert.False(host.CanBePlanned);
        Assert.Contains("behind a router", host.PlanningObstacle!, StringComparison.Ordinal);
        Assert.Contains("(routed)", result.Summary, StringComparison.Ordinal);
    }

    /// <summary>A range typed for the adapter's own cable is not routed: ARP and MACs work as normal.</summary>
    [Fact]
    public async Task ATypedRangeOnTheAdaptersOwnSubnetIsNotRouted()
    {
        var pinger = new FakePinger { Delay = TimeSpan.Zero };
        var arp = new FakeArpLookup(new ArpEntry(IPAddress.Parse("192.168.1.12"), DriveMac, 12, ArpEntryType.Dynamic));
        var sweep = new SubnetSweep(pinger, arp, new FakeOuiLookup(), Quick);

        SubnetSweepResult result = await sweep.SweepRangeAsync(Nics.Healthy(), Range("192.168.1.8-12"));

        Assert.False(result.IsRouted);
        Assert.DoesNotContain(IPAddress.Parse("192.168.1.10"), pinger.Asked);   // this PC's own
        SweptHost host = Assert.Single(result.Hosts);
        Assert.Equal(DriveMac, host.Mac);
        Assert.Equal("ARP only", host.HowFound);
    }

    /// <summary>
    /// A range on the adapter's second address is on the cable too - the second address is what
    /// makes it so.
    /// </summary>
    [Fact]
    public async Task ARangeOnTheSecondAddressIsNotRouted()
    {
        var pinger = new FakePinger { Delay = TimeSpan.Zero }.Answers("192.168.0.20", PingStatus.Replied);
        var arp = new FakeArpLookup(new ArpEntry(IPAddress.Parse("192.168.0.20"), DriveMac, 12, ArpEntryType.Dynamic));
        var sweep = new SubnetSweep(pinger, arp, new FakeOuiLookup(), Quick);
        NicInfo nic = Nics.Healthy() with { ExtraAddresses = new NicAddressList([Subnet("192.168.0.10", "255.255.255.0")]) };

        SubnetSweepResult result = await sweep.SweepRangeAsync(nic, Range("192.168.0.16-31"));

        Assert.False(result.IsRouted);
        Assert.Equal(DriveMac, Assert.Single(result.Hosts).Mac);
    }

    /// <summary>
    /// Devices behind a router are identified by name, one datagram each - never by a broadcast,
    /// which would stop at the router anyway - and their rows say why there is no MAC.
    /// </summary>
    [Fact]
    public async Task IdentifiesRoutedDevicesByUnicastOnly()
    {
        var scanner = new FakeIdentityScanner(Identities.Reply("10.20.5.40", product: "PowerFlex 525"));
        var discovery = new DeviceDiscovery(scanner, FakeArpLookup.Empty(), new FakeOuiLookup());

        DiscoveryResult result = await discovery.IdentifyAsync(Nics.Healthy(), [IPAddress.Parse("10.20.5.40")]);

        Assert.Equal(0, scanner.Scans);
        Assert.Equal([IPAddress.Parse("10.20.5.40")], scanner.Probed);

        DiscoveredDevice device = Assert.Single(result.Devices);
        Assert.Equal("PowerFlex 525", device.ProductName);
        Assert.Equal(MacSource.Routed, device.MacSource);
        Assert.False(device.CanBePlanned);
    }

    private static SweepRange Range(string text)
    {
        Assert.True(SweepRange.TryParse(text, 1024, out SweepRange? range, out string? problem), problem);
        return range!;
    }

    private static Ipv4Subnet Subnet(string address, string mask)
    {
        Assert.True(Ipv4Subnet.TryCreate(IPAddress.Parse(address), IPAddress.Parse(mask), out Ipv4Subnet subnet));
        return subnet;
    }
}
