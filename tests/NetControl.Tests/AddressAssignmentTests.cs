using System.Net;
using NetControl.Core;
using NetControl.Core.Commissioning;
using Xunit;

namespace NetControl.Tests;

/// <summary>
/// The Assign IP dialog's rules: what a person types in the Rockwell-style box, and every way it can
/// be wrong before anything is sent.
/// </summary>
public class AddressAssignmentTests
{
    private static readonly IReadOnlyList<Ipv4Subnet> Laptop = [Subnet("192.168.1.10", "255.255.255.0")];

    [Fact]
    public void TakesAnAddressMaskAndGateway()
    {
        Assert.True(AddressAssignment.TryCreate(
            "192.168.1.51", "255.255.255.0", "192.168.1.1", makeStatic: true, Laptop,
            out AddressAssignment? assignment, out IReadOnlyList<string> problems));

        Assert.Empty(problems);
        Assert.Equal(IPAddress.Parse("192.168.1.51"), assignment!.Ip);
        Assert.Equal(IPAddress.Parse("255.255.255.0"), assignment.Mask);
        Assert.Equal(IPAddress.Parse("192.168.1.1"), assignment.Gateway);
        Assert.True(assignment.MakeStatic);
    }

    [Fact]
    public void ABlankGatewayMeansNone()
    {
        Assert.True(AddressAssignment.TryCreate(
            " 192.168.1.51 ", "255.255.255.0", "  ", makeStatic: true, Laptop,
            out AddressAssignment? assignment, out _));

        Assert.Null(assignment!.Gateway);
    }

    [Theory]
    [InlineData("", "Type the IP address.")]
    [InlineData("192.168.1", "not an IPv4 IP address")]
    [InlineData("10", "not an IPv4 IP address")]
    [InlineData("192.168.1.256", "not an IPv4 IP address")]
    [InlineData("192.168.1.255", "broadcast")]
    [InlineData("192.168.1.10", "this PC's own address")]
    public void RefusesAnAddressThatIsNotOneOtherHost(string ip, string expected)
    {
        Assert.False(AddressAssignment.TryCreate(
            ip, "255.255.255.0", string.Empty, makeStatic: false, Laptop, out _, out IReadOnlyList<string> problems));

        Assert.Contains(problems, p => p.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void RefusesAMaskWithAHoleInIt()
    {
        Assert.False(AddressAssignment.TryCreate(
            "192.168.1.51", "255.0.255.0", string.Empty, makeStatic: false, Laptop, out _, out IReadOnlyList<string> problems));

        Assert.Contains(problems, p => p.Contains("contiguous", StringComparison.Ordinal));
    }

    [Fact]
    public void RefusesAGatewayTheDeviceCouldNeverReach()
    {
        Assert.False(AddressAssignment.TryCreate(
            "192.168.1.51", "255.255.255.0", "10.0.0.1", makeStatic: false, Laptop, out _, out IReadOnlyList<string> problems));

        Assert.Contains(problems, p => p.Contains("could never reach it", StringComparison.Ordinal));
    }

    /// <summary>
    /// Disabling BOOTP/DHCP is a TCP connection to the new address, and a laptop with no address on
    /// that subnet cannot make it. Refused before the device is served something nobody can finish.
    /// </summary>
    [Fact]
    public void RefusesToMakeStaticAnAddressThisPcCannotReach()
    {
        Assert.False(AddressAssignment.TryCreate(
            "10.20.30.40", "255.255.255.0", string.Empty, makeStatic: true, Laptop, out _, out IReadOnlyList<string> problems));

        string problem = Assert.Single(problems);
        Assert.Contains("cannot reach 10.20.30.40", problem, StringComparison.Ordinal);
        Assert.Contains("192.168.1.0/24", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void ServingOnlyMayGoToAnotherSubnet()
    {
        Assert.True(AddressAssignment.TryCreate(
            "10.20.30.40", "255.255.255.0", string.Empty, makeStatic: false, Laptop, out _, out _));
    }

    [Fact]
    public void ASecondAdapterAddressCountsAsReachable()
    {
        IReadOnlyList<Ipv4Subnet> twoSubnets = [.. Laptop, Subnet("10.20.30.5", "255.255.255.0")];

        Assert.True(AddressAssignment.TryCreate(
            "10.20.30.40", "255.255.255.0", string.Empty, makeStatic: true, twoSubnets, out _, out _));
    }

    [Fact]
    public void ListsEveryProblemAtOnce()
    {
        Assert.False(AddressAssignment.TryCreate(
            "nope", "", "also nope", makeStatic: true, Laptop, out _, out IReadOnlyList<string> problems));

        Assert.Equal(3, problems.Count);
    }

    private static Ipv4Subnet Subnet(string address, string mask)
    {
        Assert.True(Ipv4Subnet.TryCreate(IPAddress.Parse(address), IPAddress.Parse(mask), out Ipv4Subnet subnet));
        return subnet;
    }
}
