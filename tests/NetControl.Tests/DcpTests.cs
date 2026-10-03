using System.Net;
using NetControl.Core;
using NetControl.Core.Capture;
using NetControl.Core.Profinet;
using Xunit;

namespace NetControl.Tests;

/// <summary>
/// PROFINET DCP: the frames as literal bytes, the station-name rules, and the client against a
/// simulated segment - which also records every frame sent, so "a Set goes to one device and
/// nowhere else" is asserted on the wire.
/// </summary>
public class DcpTests
{
    private static readonly MacAddress Et200 = MacAddress.Parse("00:1B:1B:AA:BB:01");
    private static readonly MacAddress Other = MacAddress.Parse("00:1B:1B:AA:BB:02");

    [Fact]
    public void IdentifyAllIsOneMulticastWithTheAllSelector()
    {
        byte[] frame = DcpFrame.IdentifyAll(SimulatedDcpNetwork.Laptop, 0x01020304, 0x0040);

        Assert.Equal(new byte[] { 0x01, 0x0E, 0xCF, 0x00, 0x00, 0x00 }, frame[..6]);
        Assert.Equal(new byte[] { 0x88, 0x92 }, frame[12..14]);
        Assert.Equal(
            new byte[] { 0xFE, 0xFE, 0x05, 0x00, 0x01, 0x02, 0x03, 0x04, 0x00, 0x40, 0x00, 0x04, 0xFF, 0xFF, 0x00, 0x00 },
            frame[14..]);
    }

    [Fact]
    public void SetNameCarriesThePermanentQualifierAndPadsAnOddLength()
    {
        byte[] frame = DcpFrame.SetName(Et200, SimulatedDcpNetwork.Laptop, 7, "io1", permanent: true);

        Assert.Equal(Et200.ToArray(), frame[..6]);

        // FrameID FEFD, Set, request, xid 7, no delay, length 8; block 02 02, length 5, qualifier 0001, "io1", pad.
        Assert.Equal(
            new byte[] { 0xFE, 0xFD, 0x04, 0x00, 0, 0, 0, 7, 0, 0, 0, 8, 0x02, 0x02, 0x00, 0x05, 0x00, 0x01, (byte)'i', (byte)'o', (byte)'1', 0x00 },
            frame[14..]);
    }

    [Fact]
    public void SetIpCarriesAddressMaskAndGatewayBigEndian()
    {
        byte[] frame = DcpFrame.SetIp(Et200, SimulatedDcpNetwork.Laptop, 1,
            IPAddress.Parse("192.168.0.10"), IPAddress.Parse("255.255.255.0"), IPAddress.Parse("192.168.0.1"), permanent: false);

        Assert.Equal(
            new byte[] { 0x01, 0x02, 0x00, 0x0E, 0x00, 0x00, 192, 168, 0, 10, 255, 255, 255, 0, 192, 168, 0, 1 },
            frame[26..]);
    }

    [Theory]
    [InlineData("io-device-1", null)]
    [InlineData("line1.conveyor.drive-3", null)]
    [InlineData("", "needed")]
    [InlineData("IO1", "Lowercase")]
    [InlineData("-io1", "not starting")]
    [InlineData("io..1", "No empty parts")]
    [InlineData("port-001", "port-nnn")]
    [InlineData("192.168.0.1", "IP address")]
    [InlineData("io_1", "letters a-z")]
    public void StationNamesFollowTheRules(string name, string? problemContains)
    {
        string? problem = DcpStationName.Problem(name);

        if (problemContains is null)
        {
            Assert.Null(problem);
        }
        else
        {
            Assert.Contains(problemContains, problem, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task IdentifyFindsEveryDeviceWithItsNameAndAddress()
    {
        SimulatedDcpNetwork network = Network();

        IReadOnlyList<DcpDevice> found = await new DcpClient(network).IdentifyAsync(TimeSpan.FromMilliseconds(50));

        Assert.Equal(2, found.Count);
        DcpDevice named = Assert.Single(found, d => d.HasName);
        Assert.Equal("drive-1", named.NameOfStation);
        Assert.Equal(IPAddress.Parse("192.168.0.20"), named.Address);
        Assert.Equal("002A:0313", named.IdText);
        Assert.Equal("static", named.AddressOrigin);
    }

    [Fact]
    public async Task ASetGoesToOneDeviceAndIsReadBack()
    {
        SimulatedDcpNetwork network = Network();
        var client = new DcpClient(network);

        DcpSetResult result = await client.SetNameAsync(Et200, "io-1", permanent: true);
        DcpDevice? after = await client.GetAsync(Et200);

        Assert.True(result.Accepted);
        Assert.Equal("io-1", after?.NameOfStation);
        Assert.Equal("drive-1", network.Devices.Single(d => d.Mac == Other).Name);

        // Two frames: the Set and the Get, both to that one device's own MAC.
        Assert.All(network.Sent, f => Assert.Equal(Et200.ToArray(), f[..6]));
    }

    [Fact]
    public async Task ARefusalIsWordedFromTheDevicesOwnCode()
    {
        SimulatedDcpNetwork network = Network();
        network.Devices[0].RefuseWith = 6;

        DcpSetResult result = await new DcpClient(network).SetNameAsync(Et200, "io-1", permanent: true);

        Assert.False(result.Accepted);
        Assert.Contains("in operation", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefusesAnInvalidNameAndAMulticastTargetWithNothingSent()
    {
        SimulatedDcpNetwork network = Network();
        var client = new DcpClient(network);

        await Assert.ThrowsAsync<ArgumentException>(() => client.SetNameAsync(Et200, "Bad Name", permanent: true));
        await Assert.ThrowsAsync<ArgumentException>(() => client.SetNameAsync(DcpFrame.IdentifyMulticast, "io-1", permanent: true));
        await Assert.ThrowsAsync<ArgumentException>(() => client.SetIpAsync(
            Et200, IPAddress.Parse("192.168.0.10"), IPAddress.Parse("255.255.255.0"), IPAddress.Parse("10.0.0.1"), permanent: true));

        Assert.Empty(network.Sent);
    }

    [Fact]
    public async Task NoAnswerToASetIsNotASuccess()
    {
        SimulatedDcpNetwork network = Network();
        var client = new DcpClient(network) { ReplyTimeout = TimeSpan.FromMilliseconds(100) };

        DcpSetResult result = await client.SignalAsync(MacAddress.Parse("00:1B:1B:00:00:99"));

        Assert.False(result.Accepted);
        Assert.Null(result.ErrorCode);
    }

    [Fact]
    public void ParsesAnIdentifyResponseWithAConflictBit()
    {
        var device = new SimulatedDcpNetwork.Device
        {
            Mac = Et200,
            Name = "io-1",
            Address = IPAddress.Parse("192.168.0.10"),
            Mask = IPAddress.Parse("255.255.255.0"),
        };
        byte[] blocks = device.Blocks();
        blocks[^13] = 0x81;   // the IP block's BlockInfo, low byte: static, conflict detected

        byte[] payload = [0xFE, 0xFF, 0x05, 0x01, 0, 0, 0, 1, 0, 0, (byte)(blocks.Length >> 8), (byte)blocks.Length, .. blocks];

        Assert.True(DcpFrame.TryParseDevice(payload, Et200, out DcpDevice? parsed));
        Assert.True(parsed!.AddressConflict);
        Assert.Contains("ADDRESS CONFLICT", parsed.Describe(), StringComparison.Ordinal);
    }

    private static SimulatedDcpNetwork Network()
    {
        var network = new SimulatedDcpNetwork();
        network.Devices.Add(new SimulatedDcpNetwork.Device { Mac = Et200 });
        network.Devices.Add(new SimulatedDcpNetwork.Device
        {
            Mac = Other,
            Name = "drive-1",
            Address = IPAddress.Parse("192.168.0.20"),
            Mask = IPAddress.Parse("255.255.255.0"),
        });
        return network;
    }
}
