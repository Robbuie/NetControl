using System.Net;
using NetControl.Core;
using NetControl.Core.Discovery;
using NetControl.Core.Interfaces;
using Xunit;

namespace NetControl.Tests;

/// <summary>
/// Turning replies into rows: the ARP join that gives a scan result a MAC, and the wording for
/// every way that join can fail.
///
/// <para>The scanner is faked here on purpose. What a scan does on a socket is
/// <see cref="IdentityScannerTests"/>'s job; what the tool <em>concludes</em> from a set of replies
/// is this one's, and the interesting cases - an address in nobody's cache, an entry learned on the
/// wrong adapter, two devices answering as the same address - cannot be arranged on a real
/// network to order.</para>
/// </summary>
public class DeviceDiscoveryTests
{
    private static readonly MacAddress DeviceMac = MacAddress.Parse("00:1D:9C:C7:B0:70");
    private static readonly IPAddress DeviceIp = IPAddress.Parse("192.168.1.51");

    /// <summary>The ordinary case: the reply just populated the cache, so the MAC is there for free.</summary>
    [Fact]
    public async Task ResolvesTheMacFromTheArpEntryTheReplyItselfCreated()
    {
        var discovery = new DeviceDiscovery(
            new FakeIdentityScanner(Identities.Reply()),
            FakeArpLookup.Holding("192.168.1.51", DeviceMac),
            new FakeOuiLookup());

        DiscoveryResult result = await discovery.ScanAsync(Nics.Healthy());

        DiscoveredDevice device = Assert.Single(result.Devices);
        Assert.Equal(DeviceMac, device.Mac);
        Assert.Equal(MacSource.Arp, device.MacSource);
        Assert.Equal("Rockwell Automation", device.Vendor);
        Assert.True(device.CanBePlanned);
        Assert.Null(device.PlanningObstacle);
    }

    /// <summary>
    /// The plan is keyed on MAC, so a device without one is a device the plan has nowhere to put -
    /// and the result has to say so in a sentence naming what to do about it, not by being absent.
    /// </summary>
    [Fact]
    public async Task SaysWhyADeviceMissingFromTheCacheCannotBePlanned()
    {
        var discovery = new DeviceDiscovery(
            new FakeIdentityScanner(Identities.Reply()), FakeArpLookup.Empty(), FakeOuiLookup.Unknown());

        DiscoveryResult result = await discovery.ScanAsync(Nics.Healthy());

        DiscoveredDevice device = Assert.Single(result.Devices);
        Assert.True(device.Mac.IsEmpty);
        Assert.Equal(MacSource.NotInArp, device.MacSource);
        Assert.False(device.CanBePlanned);
        Assert.Contains("not in this machine's ARP cache", device.PlanningObstacle, StringComparison.Ordinal);
        Assert.Single(result.Unresolved);
    }

    /// <summary>
    /// An engineering laptop holds ARP entries for a dozen adapters, and a VPN can easily carry an
    /// entry for the same address as the port in your hand. Taking that MAC would produce a plan
    /// row keyed on the wrong hardware - a device that is never served, for a reason nobody can see.
    /// </summary>
    [Fact]
    public async Task WillNotTakeAMacLearnedOnADifferentAdapter()
    {
        var discovery = new DeviceDiscovery(
            new FakeIdentityScanner(Identities.Reply()),
            FakeArpLookup.Holding("192.168.1.51", DeviceMac, interfaceIndex: 27),
            new FakeOuiLookup());

        DiscoveryResult result = await discovery.ScanAsync(Nics.Healthy(index: 12));

        DiscoveredDevice device = Assert.Single(result.Devices);
        Assert.True(device.Mac.IsEmpty);
        Assert.Equal(MacSource.ArpOnAnotherAdapter, device.MacSource);
        Assert.False(device.CanBePlanned);
        Assert.Contains("different adapter", device.PlanningObstacle, StringComparison.Ordinal);
    }

    /// <summary>An entry Windows has kept but knows is stale is not a hardware address.</summary>
    [Fact]
    public async Task IgnoresAnInvalidArpEntry()
    {
        var discovery = new DeviceDiscovery(
            new FakeIdentityScanner(Identities.Reply()),
            new FakeArpLookup(new ArpEntry(DeviceIp, DeviceMac, 12, ArpEntryType.Invalid)),
            new FakeOuiLookup());

        DiscoveryResult result = await discovery.ScanAsync(Nics.Healthy());

        Assert.Equal(MacSource.NotInArp, Assert.Single(result.Devices).MacSource);
    }

    /// <summary>
    /// The fault only a scan can find. Two devices on one address is why replies are never
    /// collapsed by address alone, and why both are reported rather than one silently winning.
    /// </summary>
    [Fact]
    public async Task ReportsTwoDevicesAnsweringOnTheSameAddress()
    {
        var discovery = new DeviceDiscovery(
            new FakeIdentityScanner(
                Identities.Reply(serial: 0x00C0FFEE),
                Identities.Reply(serial: 0x0BADF00D, product: "PowerFlex 525")),
            FakeArpLookup.Empty(),
            FakeOuiLookup.Unknown());

        DiscoveryResult result = await discovery.ScanAsync(Nics.Healthy());

        Assert.Equal(2, result.Devices.Count);
        Assert.All(result.Devices, d => Assert.True(d.AddressIsContested));
        Assert.Equal(DeviceIp, Assert.Single(result.ContestedAddresses));
        Assert.Contains("More than one device answered on 192.168.1.51", result.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// "We did not look" and "it was not there" are different answers with different next actions,
    /// which is the same rule <c>PortConflictDetector</c> follows about a check it could not run.
    /// </summary>
    [Fact]
    public async Task SaysTheCacheWasNotReadRatherThanThatTheDeviceWasMissingFromIt()
    {
        var discovery = new DeviceDiscovery(
            new FakeIdentityScanner(Identities.Reply()),
            FakeArpLookup.Unavailable(),
            FakeOuiLookup.Unknown());

        DiscoveryResult result = await discovery.ScanAsync(Nics.Healthy());

        DiscoveredDevice device = Assert.Single(result.Devices);
        Assert.Equal(MacSource.NotChecked, device.MacSource);
        Assert.Contains("cannot be read on this machine", device.PlanningObstacle, StringComparison.Ordinal);
    }

    /// <summary>A scan that found nothing is still a scan that transmitted, so it still reports.</summary>
    [Fact]
    public async Task ReportsAScanThatFoundNothing()
    {
        var discovery = new DeviceDiscovery(
            new FakeIdentityScanner(), FakeArpLookup.Empty(), FakeOuiLookup.Unknown());

        DiscoveryResult result = await discovery.ScanAsync(Nics.Healthy());

        Assert.Empty(result.Devices);
        Assert.Contains("0 device(s) answered", result.Summary, StringComparison.Ordinal);
    }

    /// <summary>The vendor comes off the MAC, so no MAC means no vendor rather than a guess.</summary>
    [Fact]
    public async Task LeavesTheVendorBlankWhenThereIsNoMacToLookItUpFrom()
    {
        var discovery = new DeviceDiscovery(
            new FakeIdentityScanner(Identities.Reply()), FakeArpLookup.Empty(), new FakeOuiLookup());

        DiscoveryResult result = await discovery.ScanAsync(Nics.Healthy());

        Assert.Null(Assert.Single(result.Devices).Vendor);
    }

    /// <summary>
    /// A device answering from somewhere other than the address it claims. The ARP join follows the
    /// sender, because that is the address whose entry the reply itself created.
    /// </summary>
    [Fact]
    public async Task JoinsOnTheAddressTheReplyCameFromRatherThanTheOneItClaims()
    {
        var discovery = new DeviceDiscovery(
            new FakeIdentityScanner(Identities.Reply(address: "192.168.1.51", sender: "192.168.1.77")),
            FakeArpLookup.Holding("192.168.1.77", DeviceMac),
            new FakeOuiLookup());

        DiscoveryResult result = await discovery.ScanAsync(Nics.Healthy());

        DiscoveredDevice device = Assert.Single(result.Devices);
        Assert.Equal(DeviceMac, device.Mac);
        Assert.Equal(MacSource.Arp, device.MacSource);
        Assert.False(device.Identity.AddressMatchesSender);
    }
}
