using System.Net;
using NetControl.Core.Cip;
using NetControl.Core.DeviceHealth;
using NetControl.Core.Enip;
using NetControl.Core.Persistence;
using NetControl.Core.Reachability;
using Xunit;
using SimMethod = NetControl.DeviceSim.ConfigMethod;
using SimQuirk = NetControl.DeviceSim.Quirk;

namespace NetControl.Tests;

/// <summary>
/// The diagnostics read, end to end against a simulated adapter over a real socket on loopback -
/// so the EPATH to each instance, the little-endian decode of every counter and the walk across
/// ports are exercised rather than asserted about.
/// </summary>
public class DeviceHealthReaderTests
{
    [Fact]
    public async Task ReadsWhoTheDeviceIsHowItIsAddressedAndItsPort()
    {
        await using SimulatedAdapter adapter = await SimulatedAdapter.StartAsync();

        DeviceHealthReport report = await Reader(adapter).ReadAsync(SimulatedAdapter.Address);

        Assert.Equal("SIM-ENBT/A Simulated Adapter", report.ProductName);
        Assert.Equal("11.03", report.Revision);
        Assert.Equal(0x00C0FFEEu, report.SerialNumber);
        Assert.Equal(ConfigMethod.Bootp, report.Method);
        Assert.Equal(IPAddress.Parse("192.168.1.51"), report.Configuration?.Ip);
        Assert.Equal("sim-adapter", report.HostName);
        Assert.Equal(1, report.TcpIpStatus?.ConfigurationStatus);

        LinkPort port = Assert.Single(report.Ports);
        Assert.Equal(100u, port.SpeedMbps);
        Assert.True(port.Flags?.FullDuplex);
        Assert.Equal(NegotiationStatus.Negotiated, port.Flags?.Negotiation);
        Assert.Equal(adapter.Device.MacString, port.Mac?.ToString());
        Assert.Equal(5_000u, port.Interface?.InUnicastPackets);
        Assert.Equal(0u, port.Media?.FcsErrors);
    }

    /// <summary>The fault the tab exists for, read off the wire and then named.</summary>
    [Fact]
    public async Task FindsADuplexMismatch()
    {
        await using SimulatedAdapter adapter = await SimulatedAdapter.StartAsync(SimQuirk.DuplexMismatch);

        DeviceHealthReport report = await Reader(adapter).ReadAsync(SimulatedAdapter.Address);
        DeviceHealthResult result = DeviceHealthAssessment.Assess(report);

        Assert.False(report.Ports[0].Flags?.FullDuplex);
        Assert.Equal(37u, report.Ports[0].Media?.LateCollisions);
        Assert.Contains(result.Findings, f => f.Message.Contains("half duplex", StringComparison.Ordinal));
        Assert.Contains(result.Findings, f => f.Message.Contains("late collision", StringComparison.Ordinal));
    }

    /// <summary>Ports are found by asking upward until the device says there is no such instance.</summary>
    [Fact]
    public async Task FindsEveryPortOfADeviceWithAnEmbeddedSwitch()
    {
        await using SimulatedAdapter adapter = await SimulatedAdapter.StartAsync(SimQuirk.TwoPorts);

        DeviceHealthReport report = await Reader(adapter).ReadAsync(SimulatedAdapter.Address);

        Assert.Equal(2, report.Ports.Count);
        Assert.False(report.Ports[1].HasLink);
    }

    /// <summary>
    /// The counters are optional in the spec and plenty of small devices refuse them. That is a note,
    /// not a failed read: speed and duplex are still there.
    /// </summary>
    [Fact]
    public async Task ADeviceThatRefusesItsCountersStillGetsItsLinkReported()
    {
        await using SimulatedAdapter adapter = await SimulatedAdapter.StartAsync(SimQuirk.NoLinkCounters);

        DeviceHealthReport report = await Reader(adapter).ReadAsync(SimulatedAdapter.Address);

        LinkPort port = Assert.Single(report.Ports);
        Assert.Equal(100u, port.SpeedMbps);
        Assert.Null(port.Media);
        Assert.Contains(port.Refused, r => r.StartsWith("media counters", StringComparison.Ordinal));
    }

    /// <summary>Two reads a moment apart, with the counter moving in between, is "still counting".</summary>
    [Fact]
    public async Task ASecondReadSeesWhatMoved()
    {
        await using SimulatedAdapter adapter = await SimulatedAdapter.StartAsync();
        var clock = new TestTimeProvider();
        var reader = new DeviceHealthReader(clock) { Port = adapter.Port, ConnectTimeout = TimeSpan.FromSeconds(2) };

        DeviceHealthReport first = await reader.ReadAsync(SimulatedAdapter.Address);

        adapter.Device.LinkPorts[0].MediaCounters[1] += 25;   // FCS errors, the second UDINT
        clock.Advance(TimeSpan.FromSeconds(30));

        DeviceHealthReport second = await reader.ReadAsync(SimulatedAdapter.Address);
        DeviceHealthResult result = DeviceHealthAssessment.Assess(second, first);

        // The simulator also answers on loopback while claiming 192.168.1.51, which is its own Warn.
        HealthFinding finding = Assert.Single(
            result.Findings, f => f.Message.Contains("arrived damaged", StringComparison.Ordinal));
        Assert.Equal(EventSeverity.Warn, finding.Severity);
        Assert.Contains("25 frame(s) arrived damaged", finding.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A written configuration the device is holding until a reset shows up in its TCP/IP status -
    /// which is the explanation for a Set static that came back unverified.
    /// </summary>
    [Fact]
    public async Task SeesAConfigurationWaitingForAReset()
    {
        await using SimulatedAdapter adapter = await SimulatedAdapter.StartAsync(SimQuirk.RequiresResetToApply);
        adapter.Device.Method = SimMethod.Static;
        adapter.Device.ApplyInterfaceConfig(Config("192.168.1.60"));

        DeviceHealthReport report = await Reader(adapter).ReadAsync(SimulatedAdapter.Address);

        Assert.True(report.TcpIpStatus?.ConfigurationPending);
    }

    /// <summary>Nothing is sent to an address that is not one host - not even the connect.</summary>
    [Fact]
    public async Task RefusesABroadcastBeforeConnecting()
    {
        await Assert.ThrowsAsync<ReachabilityException>(
            () => new DeviceHealthReader().ReadAsync(IPAddress.Broadcast));
    }

    /// <summary>Nothing listening is an ordinary EtherNet/IP failure, with Core's own wording.</summary>
    [Fact]
    public async Task SaysSoWhenNothingAnswers()
    {
        var reader = new DeviceHealthReader { Port = 1, ConnectTimeout = TimeSpan.FromSeconds(1) };

        await Assert.ThrowsAsync<EnipException>(() => reader.ReadAsync(IPAddress.Loopback));
    }

    private static DeviceHealthReader Reader(SimulatedAdapter adapter) =>
        new() { Port = adapter.Port, ConnectTimeout = TimeSpan.FromSeconds(2) };

    private static byte[] Config(string ip)
    {
        var configuration = new InterfaceConfig
        {
            Ip = IPAddress.Parse(ip),
            Mask = IPAddress.Parse("255.255.255.0"),
        };

        return configuration.Serialize();
    }
}
