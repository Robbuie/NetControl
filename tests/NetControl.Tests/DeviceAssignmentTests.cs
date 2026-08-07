using System.Net;
using NetControl.Core;
using NetControl.Core.Dhcp;
using Xunit;

namespace NetControl.Tests;

public class DeviceAssignmentTests
{
    private static readonly MacAddress Mac = MacAddress.Parse(Frames.DeviceMac);

    [Fact]
    public void RejectsANonContiguousMask()
    {
        // 255.0.255.0 is the classic typo for 255.255.255.0 and it is worth catching at import
        // time rather than after a panel has been half-configured.
        ArgumentException ex = Assert.Throws<ArgumentException>(() => new DeviceAssignment(
            Mac, IPAddress.Parse("192.168.1.51"), IPAddress.Parse("255.0.255.0")));

        Assert.Contains("contiguous", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("255.255.255.0")]
    [InlineData("255.255.0.0")]
    [InlineData("255.255.255.252")]
    [InlineData("255.255.255.255")]
    [InlineData("0.0.0.0")]
    public void AcceptsEveryContiguousMask(string mask)
    {
        var assignment = new DeviceAssignment(Mac, IPAddress.Parse("192.168.1.51"), IPAddress.Parse(mask));
        Assert.Equal(IPAddress.Parse(mask), assignment.SubnetMask);
    }

    [Fact]
    public void RejectsAnIPv6Address() => Assert.Throws<ArgumentException>(() => new DeviceAssignment(
        Mac, IPAddress.Parse("fe80::1"), IPAddress.Parse("255.255.255.0")));

    [Fact]
    public void RejectsAMacThatCannotIdentifyADevice()
    {
        IPAddress ip = IPAddress.Parse("192.168.1.51");
        IPAddress mask = IPAddress.Parse("255.255.255.0");

        Assert.Throws<ArgumentException>(() => new DeviceAssignment(MacAddress.Empty, ip, mask));
        Assert.Throws<ArgumentException>(() => new DeviceAssignment(MacAddress.Parse("FF:FF:FF:FF:FF:FF"), ip, mask));
        Assert.Throws<ArgumentException>(() => new DeviceAssignment(MacAddress.Parse("01:00:5E:00:00:01"), ip, mask));
    }

    [Fact]
    public void TreatsAZeroGatewayAsNoGateway()
    {
        var assignment = new DeviceAssignment(
            Mac, IPAddress.Parse("192.168.1.51"), IPAddress.Parse("255.255.255.0"), IPAddress.Any);

        // Serving 0.0.0.0 as a router option is worse than serving nothing.
        Assert.Null(assignment.Gateway);
    }

    [Fact]
    public void KnowsWhetherTheLaptopCouldStillReachTheDevice()
    {
        var assignment = new DeviceAssignment(
            Mac, IPAddress.Parse("192.168.1.51"), IPAddress.Parse("255.255.255.0"));

        Assert.True(assignment.IsOnSameSubnetAs(IPAddress.Parse("192.168.1.10"), IPAddress.Parse("255.255.255.0")));
        Assert.False(assignment.IsOnSameSubnetAs(IPAddress.Parse("10.0.0.10"), IPAddress.Parse("255.255.255.0")));
        Assert.False(assignment.IsOnSameSubnetAs(null, null));
    }
}
