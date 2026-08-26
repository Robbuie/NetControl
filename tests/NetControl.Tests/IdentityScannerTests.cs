using System.Net;
using NetControl.Core.Enip;
using NetControl.Core.Interfaces;
using Xunit;
using SimQuirk = NetControl.DeviceSim.Quirk;

namespace NetControl.Tests;

/// <summary>
/// The scanner, driven against <c>NetControl.DeviceSim</c> over a real UDP socket on loopback.
///
/// <para><b>No test here broadcasts.</b> Every one of them sets <c>Broadcast = false</c> and hands
/// the scanner an explicit loopback target, because <c>dotnet test</c> runs on whatever network the
/// machine happens to be plugged into and a build must never put discovery traffic on a plant
/// segment. The broadcast path is the same three lines as the unicast one and is exercised by the
/// bench session instead - see BENCH.md.</para>
/// </summary>
public class IdentityScannerTests
{
    /// <summary>Loopback, so the scanner binds somewhere the simulator can answer.</summary>
    private static NicInfo Loopback() => Nics.Healthy(address: "127.0.0.1", mask: "255.0.0.0");

    private static IdentityScanner Scanner(int port) => new(new ScanOptions
    {
        Port = port,
        Broadcast = false,
        Window = TimeSpan.FromSeconds(1),
        UnicastPace = TimeSpan.Zero,
    });

    [Fact]
    public async Task FindsADeviceThatAlreadyHasAnAddress()
    {
        await using SimulatedAdapter adapter = await SimulatedAdapter.StartAsync();

        ScanReport report = await Scanner(adapter.Port).ScanAsync(Loopback(), [IPAddress.Loopback]);

        ListIdentityReply reply = Assert.Single(report.Replies);
        Assert.Equal("SIM-ENBT/A Simulated Adapter", reply.ProductName);
        Assert.Equal(0x00C0FFEEu, reply.SerialNumber);
        Assert.Equal(1, report.ProbesSent);
        Assert.Equal(0, report.Refused);
    }

    /// <summary>
    /// The simulator answers from loopback while reporting the address it is configured with, which
    /// is exactly the shape of a real disagreement between where a device is and where it thinks it
    /// is. Both have to survive the scan.
    /// </summary>
    [Fact]
    public async Task KeepsTheAddressTheDeviceClaimsApartFromTheOneItAnsweredFrom()
    {
        await using SimulatedAdapter adapter = await SimulatedAdapter.StartAsync();

        ScanReport report = await Scanner(adapter.Port).ScanAsync(Loopback(), [IPAddress.Loopback]);

        ListIdentityReply reply = Assert.Single(report.Replies);
        Assert.Equal(IPAddress.Parse("192.168.1.51"), reply.Address);
        Assert.Equal(IPAddress.Loopback, reply.SenderAddress);
        Assert.False(reply.AddressMatchesSender);
    }

    /// <summary>
    /// A device probed twice - which happens for real the moment a unicast sweep covers an address
    /// that also answered the broadcast - is one device, not two.
    /// </summary>
    [Fact]
    public async Task CollapsesADeviceThatAnswersMoreThanOnce()
    {
        await using SimulatedAdapter adapter = await SimulatedAdapter.StartAsync();

        ScanReport report = await Scanner(adapter.Port)
            .ScanAsync(Loopback(), [IPAddress.Loopback, IPAddress.Loopback]);

        Assert.Single(report.Replies);
        Assert.Equal(2, report.ProbesSent);
        Assert.Equal(1, report.Duplicates);
    }

    /// <summary>
    /// The safety property, asserted rather than assumed: a scan narrowed down to nothing puts
    /// nothing on the wire. It does not quietly fall back to a broadcast.
    /// </summary>
    [Fact]
    public async Task TransmitsNothingWhenThereIsNothingToProbe()
    {
        var scanner = new IdentityScanner(new ScanOptions { Broadcast = false, Window = TimeSpan.FromSeconds(5) });

        ScanReport report = await scanner.ScanAsync(Loopback());

        Assert.Equal(0, report.ProbesSent);
        Assert.Empty(report.Replies);
        Assert.Equal(TimeSpan.Zero, report.Duration);
    }

    /// <summary>
    /// A sweep is a list of hosts. The moment a broadcast address gets into it, "one small datagram
    /// at a time" stops being true and every device on the segment answers at once.
    /// </summary>
    [Theory]
    [InlineData("255.255.255.255")]
    [InlineData("127.255.255.255")]     // the directed broadcast of the adapter's own /8
    [InlineData("224.0.0.1")]
    public async Task RefusesAnythingInTheSweepThatIsNotOneHost(string target)
    {
        var scanner = new IdentityScanner(new ScanOptions { Broadcast = false });

        EnipException error = await Assert.ThrowsAsync<EnipException>(
            () => scanner.ScanAsync(Loopback(), [IPAddress.Parse(target)]));

        Assert.Contains("not one device", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An adapter with no address cannot source a scan, and saying so up front is much better than
    /// a socket error two seconds later.
    /// </summary>
    [Fact]
    public async Task RefusesAnAdapterThatCannotSourceAScan()
    {
        var scanner = new IdentityScanner(new ScanOptions { Broadcast = false });

        EnipException error = await Assert.ThrowsAsync<EnipException>(
            () => scanner.ScanAsync(Nics.NoAddress(), [IPAddress.Loopback]));

        Assert.Contains("no IPv4 address", error.Message, StringComparison.Ordinal);
        Assert.NotNull(error.Remediation);
    }

    /// <summary>
    /// Some adapters answer only unicast discovery. The quirk exists so the behaviour can be
    /// reproduced at a desk; here it stands in for "nothing answered", which must be an empty
    /// result and not an error.
    /// </summary>
    [Fact]
    public async Task ReturnsNothingRatherThanFailingWhenNoDeviceAnswers()
    {
        await using SimulatedAdapter adapter = await SimulatedAdapter.StartAsync(SimQuirk.IgnoresBroadcastDiscovery);

        ScanReport report = await Scanner(adapter.Port).ScanAsync(Loopback(), [IPAddress.Loopback]);

        Assert.Empty(report.Replies);
        Assert.Equal(1, report.ProbesSent);
    }
}
