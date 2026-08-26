using System.Net;
using NetControl.Core;
using NetControl.Core.Dhcp;
using NetControl.Core.Interfaces;
using Xunit;
using Xunit.Abstractions;

namespace NetControl.Tests;

/// <summary>
/// These start a real <see cref="DhcpServer"/>, bind a real socket, and put real bytes on it.
/// Everything else in this project tests the codec and the policy in isolation; nothing else
/// tests that the socket plumbing works at all.
///
/// They are hermetic: the request goes to 127.0.0.1 and nothing is transmitted, because Watch
/// mode is the default and the one Serve-mode test here plans no matching device. The test that
/// actually broadcasts a reply is opt-in - see <see cref="DhcpServerBroadcastTests"/> - because a
/// unit test run must not put DHCP traffic on whatever network the laptop happens to be plugged
/// into.
/// </summary>
public class DhcpServerWireTests
{
    private static byte[] BootpRequest => Frames.Bytes(Frames.BootpRequest);

    private static MacAddress DeviceMac => MacAddress.Parse(Frames.DeviceMac);

    private static DeviceAssignment Planned => new(
        DeviceMac,
        IPAddress.Parse("192.168.1.51"),
        IPAddress.Parse("255.255.255.0"),
        IPAddress.Parse("192.168.1.1"));

    [Fact]
    public async Task ReceivesARealDatagramAndSaysWhichAdapterItArrivedOn()
    {
        await using WireHarness harness = await WireHarness.StartAsync(new StaticMapPolicy());

        harness.Send(BootpRequest);
        DhcpRequestEventArgs request = await harness.NextRequestAsync();

        Assert.Equal(DeviceMac, request.Mac);
        Assert.Equal("BOOTP REQUEST", request.Kind);
        Assert.Equal(IPAddress.Loopback, request.DestinationAddress);
        Assert.False(request.IsRetransmit);

        // IP_PKTINFO is the whole reason this engine binds 0.0.0.0 instead of one adapter.
        Assert.True(request.ArrivalInterfaceIndex > 0);
        Assert.Contains(request.ArrivalInterfaceIndex, harness.Inventory.Queried);
        Assert.NotNull(request.ArrivalNic);

        Assert.False(request.Decision.ShouldServe);
        Assert.Contains("not in the plan", request.Decision.Reason, StringComparison.Ordinal);
        Assert.Empty(harness.Faults());
    }

    [Fact]
    public async Task RecognisesADhcpDiscoverOffTheWire()
    {
        await using WireHarness harness = await WireHarness.StartAsync(new StaticMapPolicy());

        harness.Send(Frames.Bytes(Frames.DhcpDiscover));
        DhcpRequestEventArgs request = await harness.NextRequestAsync();

        Assert.Equal("DHCP Discover", request.Kind);
        Assert.Equal("Rockwell Automation, 1756-EN2T", request.Packet.VendorClass);
    }

    /// <summary>
    /// The safety property the product rests on. The device is in the plan, the adapter is fine,
    /// the policy says serve - and because the mode is Watch, nothing leaves the machine.
    /// </summary>
    [Fact]
    public async Task WatchModeTransmitsNothingEvenForAPlannedDevice()
    {
        await using WireHarness harness = await WireHarness.StartAsync(
            new StaticMapPolicy([Planned]),
            DhcpServerMode.Watch,
            listenAsDevice: true);

        harness.Send(BootpRequest);
        DhcpRequestEventArgs request = await harness.NextRequestAsync();

        Assert.True(request.Decision.ShouldServe);
        Assert.True(await harness.StayedSilentAsync(TimeSpan.FromMilliseconds(750)));
    }

    [Fact]
    public async Task ServeModeTransmitsNothingForAMacThatIsNotInThePlan()
    {
        await using WireHarness harness = await WireHarness.StartAsync(
            new StaticMapPolicy(),
            DhcpServerMode.Serve,
            listenAsDevice: true);

        harness.Send(BootpRequest);
        DhcpRequestEventArgs request = await harness.NextRequestAsync();

        Assert.False(request.Decision.ShouldServe);
        Assert.True(await harness.StayedSilentAsync(TimeSpan.FromMilliseconds(750)));
    }

    /// <summary>
    /// A request on an adapter the user did not pick is reported, not dropped. "Your device is
    /// asking, but on [12] vEthernet (Default Switch)" is the most useful sentence this tool can
    /// produce, and it is impossible to produce if the packet is discarded silently.
    /// </summary>
    [Fact]
    public async Task ReportsARequestThatArrivedOnAnUnselectedAdapter()
    {
        await using WireHarness harness = await WireHarness.StartAsync(
            new StaticMapPolicy([Planned]),
            DhcpServerMode.Serve,
            interfaceIndexFilter: 9999,
            listenAsDevice: true);

        harness.Send(BootpRequest);
        DhcpRequestEventArgs request = await harness.NextRequestAsync();

        Assert.False(request.Decision.ShouldServe);
        Assert.Contains("not the selected adapter", request.Decision.Reason, StringComparison.Ordinal);
        Assert.True(await harness.StayedSilentAsync(TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public async Task FlagsTheSecondCopyOfARequestAsARetransmit()
    {
        await using WireHarness harness = await WireHarness.StartAsync(new StaticMapPolicy());

        harness.Send(BootpRequest);
        harness.Send(BootpRequest);

        DhcpRequestEventArgs first = await harness.NextRequestAsync();
        DhcpRequestEventArgs second = await harness.NextRequestAsync();

        Assert.False(first.IsRetransmit);
        Assert.True(second.IsRetransmit);
    }

    /// <summary>
    /// A BOOTREPLY on port 67 is either our own broadcast looping back or another server on the
    /// segment. Either way it is not a request and must not turn into a log row.
    /// </summary>
    [Fact]
    public async Task IgnoresABootReply()
    {
        await using WireHarness harness = await WireHarness.StartAsync(new StaticMapPolicy());

        byte[] reply = BootpRequest;
        reply[0] = BootpPacket.OpBootReply;
        harness.Send(reply);
        harness.Send(BootpRequest);

        DhcpRequestEventArgs request = await harness.NextRequestAsync();

        // If the BOOTREPLY had raised an event, this would be it - and it would be op 2.
        Assert.Equal(BootpPacket.OpBootRequest, request.Packet.Op);
    }

    [Fact]
    public async Task IgnoresADatagramTooShortToBeBootp()
    {
        await using WireHarness harness = await WireHarness.StartAsync(new StaticMapPolicy());

        harness.Send(new byte[16]);
        harness.Send(BootpRequest);

        DhcpRequestEventArgs request = await harness.NextRequestAsync();

        Assert.Equal(DeviceMac, request.Mac);
        Assert.Empty(harness.Faults());
    }

    [Fact]
    public async Task RefusesToBeStartedTwice()
    {
        await using WireHarness harness = await WireHarness.StartAsync(new StaticMapPolicy());

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Server.RunAsync(CancellationToken.None));
    }
}

/// <summary>
/// The one test that actually broadcasts. Opt in with <c>NETCONTROL_WIRE_SERVE=1</c>.
///
/// It is off by default on purpose. A limited broadcast leaves the machine by a real adapter, and
/// running <c>dotnet test</c> should never put DHCP-shaped traffic onto a customer's plant network
/// just because somebody was on site with a laptop. The frame is harmless - a BOOTREPLY on a
/// random high port addressed to a MAC that is not there - but "harmless" is not the standard this
/// project holds itself to about unsolicited packets.
/// </summary>
public class DhcpServerBroadcastTests(ITestOutputHelper output)
{
    private const string OptIn = "NETCONTROL_WIRE_SERVE";

    private readonly ITestOutputHelper _output = output;

    [Fact]
    public async Task ServesAPlannedDeviceOutOfARealAdapter()
    {
        if (Environment.GetEnvironmentVariable(OptIn) != "1")
        {
            _output.WriteLine($"Skipped: set {OptIn}=1 to run this. It broadcasts on a real adapter.");
            return;
        }

        NicInfo? adapter = NicMonitor.Enumerate().FirstOrDefault(n => n.CanServe && !n.IsLikelyVirtual)
            ?? NicMonitor.Enumerate().FirstOrDefault(n => n.CanServe);

        if (adapter?.IPv4 is null || adapter.Mask is null)
        {
            _output.WriteLine("Skipped: no adapter is up with a routable IPv4 address.");
            return;
        }

        // An address on the adapter's own subnet, so the policy's reachability check passes.
        byte[] octets = adapter.IPv4.GetAddressBytes();
        octets[3] = 254;
        var plannedIp = new IPAddress(octets);

        var planned = new DeviceAssignment(
            MacAddress.Parse(Frames.DeviceMac), plannedIp, adapter.Mask);

        await using WireHarness harness = await WireHarness.StartAsync(
            new StaticMapPolicy([planned]),
            DhcpServerMode.Serve,
            nic: adapter,
            listenAsDevice: true);

        _output.WriteLine($"Serving {plannedIp} out of [{adapter.Index}] {adapter.Name} ({adapter.IPv4})");

        harness.Send(Frames.Bytes(Frames.BootpRequest));

        DhcpRequestEventArgs request = await harness.NextRequestAsync();
        Assert.True(request.Decision.ShouldServe, request.Decision.Reason);

        DhcpReplyEventArgs sent = await harness.NextReplyAsync();
        Assert.Equal("BOOTP REPLY", sent.Kind);
        Assert.Equal(BootpPacket.MinimumMessageLength, sent.ByteCount);

        byte[]? onTheWire = await harness.ReceiveAsDeviceAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(onTheWire);
        Assert.True(BootpPacket.TryParse(onTheWire, out BootpPacket? parsed));

        Assert.Equal(BootpPacket.OpBootReply, parsed!.Op);
        Assert.Equal(plannedIp, parsed.YourIp);
        Assert.Equal(adapter.IPv4, parsed.ServerIp);
        Assert.Equal(MacAddress.Parse(Frames.DeviceMac), parsed.ClientMac);
        Assert.Equal(adapter.Mask, parsed.GetAddressOption(DhcpOptionCode.SubnetMask));
    }
}
