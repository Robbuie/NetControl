using System.Net;
using NetControl.Core;
using Xunit;

namespace NetControl.Tests;

/// <summary>
/// Subnet arithmetic, which is the sort of thing that is obviously right until a /26 says a device
/// two subnets away is local and somebody spends an afternoon on it.
/// </summary>
public sealed class Ipv4SubnetTests
{
    [Theory]
    [InlineData("255.255.255.0", 24)]
    [InlineData("255.255.0.0", 16)]
    [InlineData("255.255.255.192", 26)]
    [InlineData("255.255.255.255", 32)]
    public void ReadsThePrefixLengthOffTheMask(string mask, int expected) =>
        Assert.Equal(expected, Subnet("192.168.1.10", mask).PrefixLength);

    /// <summary>
    /// A hole in the mask makes every calculation downstream of it meaningless rather than merely
    /// wrong, so it is refused at the door rather than tolerated.
    /// </summary>
    [Theory]
    [InlineData("255.255.0.255")]
    [InlineData("255.0.255.0")]
    [InlineData("0.0.0.1")]
    public void RefusesAMaskWhoseBitsAreNotContiguous(string mask)
    {
        Assert.False(Ipv4Subnet.TryCreate(IPAddress.Parse("192.168.1.10"), IPAddress.Parse(mask), out _));
        Assert.False(Ipv4Subnet.IsContiguousMask(IPAddress.Parse(mask)));
    }

    [Fact]
    public void RefusesAnythingThatIsNotIPv4()
    {
        Assert.False(Ipv4Subnet.TryCreate(IPAddress.Parse("fe80::1"), IPAddress.Parse("255.255.255.0"), out _));
        Assert.False(Ipv4Subnet.TryCreate(IPAddress.Parse("192.168.1.10"), null, out _));
        Assert.False(Ipv4Subnet.IsContiguousMask(null));
    }

    /// <summary>
    /// The mask has to be applied to every bit, not to every byte. A /26 is where a comparison that
    /// stops at byte boundaries starts answering confidently and wrongly.
    /// </summary>
    [Fact]
    public void KnowsWhatIsOnTheSegmentAndWhatIsNot()
    {
        Ipv4Subnet subnet = Subnet("192.168.1.10", "255.255.255.192");

        Assert.Equal(IPAddress.Parse("192.168.1.0"), subnet.Network);
        Assert.True(subnet.Contains(IPAddress.Parse("192.168.1.63")));
        Assert.False(subnet.Contains(IPAddress.Parse("192.168.1.64")));
        Assert.False(subnet.Contains(IPAddress.Parse("10.0.0.1")));
    }

    /// <summary>Boundaries are on the segment. Whether a device may hold one is a separate question.</summary>
    [Fact]
    public void TheNetworkAndBroadcastAddressesAreOnTheSegment()
    {
        Ipv4Subnet subnet = Subnet("192.168.1.10", "255.255.255.0");

        Assert.True(subnet.Contains(IPAddress.Parse("192.168.1.0")));
        Assert.True(subnet.Contains(IPAddress.Parse("192.168.1.255")));
    }

    [Fact]
    public void ContainsNothingThatIsNotAnIPv4Address()
    {
        Ipv4Subnet subnet = Subnet("192.168.1.10", "255.255.255.0");

        Assert.False(subnet.Contains(null));
        Assert.False(subnet.Contains(IPAddress.Parse("fe80::1")));
    }

    /// <summary>
    /// <c>default</c> is 0.0.0.0/0, which contains everything. Nothing should be reaching for a
    /// subnet it never created, but if it does, this is what it gets - so it is written down.
    /// </summary>
    [Fact]
    public void AnUninitialisedSubnetIsTheWholeAddressSpace()
    {
        Ipv4Subnet subnet = default;

        Assert.Equal(0, subnet.PrefixLength);
        Assert.True(subnet.Contains(IPAddress.Parse("10.0.0.1")));
    }

    [Fact]
    public void ReadsBackAsAddressAndPrefix() =>
        Assert.Equal("192.168.1.0/26", Subnet("192.168.1.10", "255.255.255.192").ToString());

    private static Ipv4Subnet Subnet(string address, string mask)
    {
        Assert.True(Ipv4Subnet.TryCreate(IPAddress.Parse(address), IPAddress.Parse(mask), out Ipv4Subnet subnet));
        return subnet;
    }
}
