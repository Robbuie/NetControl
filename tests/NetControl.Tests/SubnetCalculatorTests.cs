using System.Net;
using NetControl.App.ViewModels;
using NetControl.Core;
using NetControl.Core.Interfaces;
using Xunit;

namespace NetControl.Tests;

/// <summary>
/// The subnet calculator, and this PC's own adapter counters - the two small tools in F6. Both are
/// arithmetic on what somebody typed or what Windows reported; neither sends anything.
/// </summary>
public class SubnetCalculatorTests
{
    [Theory]
    [InlineData("192.168.1.51/24")]
    [InlineData("192.168.1.51 255.255.255.0")]
    [InlineData("192.168.1.51/255.255.255.0")]
    [InlineData("  192.168.1.51   /  24 ")]
    public void ReadsAnAddressWithAPrefixOrAMaskTheSameWay(string text)
    {
        Assert.True(SubnetCalculation.TryParse(text, out Ipv4Subnet subnet, out _));

        Assert.Equal(IPAddress.Parse("192.168.1.0"), subnet.Network);
        Assert.Equal(24, subnet.PrefixLength);
        Assert.Equal(IPAddress.Parse("192.168.1.255"), subnet.Broadcast);
        Assert.Equal(IPAddress.Parse("192.168.1.1"), subnet.FirstHost);
        Assert.Equal(IPAddress.Parse("192.168.1.254"), subnet.LastHost);
        Assert.Equal(254, subnet.HostCount);
        Assert.Equal(IPAddress.Parse("0.0.0.255"), subnet.Wildcard);
    }

    /// <summary>The framework reads "192.168.1" as 192.168.0.1. The calculator must refuse it instead.</summary>
    [Theory]
    [InlineData("192.168.1/24")]
    [InlineData("192.168.1.51/33")]
    [InlineData("192.168.1.51 255.0.255.0")]
    [InlineData("192.168.1.51")]
    [InlineData("conveyor/24")]
    public void RefusesWhatIsNotAnAddressAndAPrefix(string text)
    {
        Assert.False(SubnetCalculation.TryParse(text, out _, out string? problem));
        Assert.False(string.IsNullOrWhiteSpace(problem));
    }

    [Fact]
    public void ThirtyOneAndThirtyTwoHaveNoNetworkOrBroadcastToLose()
    {
        Assert.True(SubnetCalculation.TryParse("10.0.0.0/31", out Ipv4Subnet pointToPoint, out _));
        Assert.Equal(2, pointToPoint.HostCount);
        Assert.Equal(IPAddress.Parse("10.0.0.0"), pointToPoint.FirstHost);
        Assert.Equal(IPAddress.Parse("10.0.0.1"), pointToPoint.LastHost);

        Assert.True(SubnetCalculation.TryParse("10.0.0.7/32", out Ipv4Subnet host, out _));
        Assert.Equal(1, host.HostCount);
        Assert.Equal(IPAddress.Parse("10.0.0.7"), host.FirstHost);
    }

    [Fact]
    public void AnOddSizedSubnetIsWorkedOutCorrectly()
    {
        Assert.True(SubnetCalculation.TryParse("172.16.5.130/26", out Ipv4Subnet subnet, out _));

        Assert.Equal(IPAddress.Parse("172.16.5.128"), subnet.Network);
        Assert.Equal(IPAddress.Parse("172.16.5.191"), subnet.Broadcast);
        Assert.Equal(IPAddress.Parse("255.255.255.192"), subnet.Mask);
        Assert.Equal(62, subnet.HostCount);
    }

    [Fact]
    public void SlashZeroIsTheWholeAddressSpaceRatherThanAShiftBug()
    {
        Assert.True(SubnetCalculation.TryParse("10.1.2.3/0", out Ipv4Subnet subnet, out _));
        Assert.Equal(IPAddress.Any, subnet.Network);
        Assert.Equal(IPAddress.Parse("0.0.0.0"), subnet.Mask);
    }

    /// <summary>The question it is usually opened for: is that device on the same subnet as this one?</summary>
    [Fact]
    public void SaysWhetherAnotherAddressIsOnTheSubnet()
    {
        var calculator = new SubnetCalculatorViewModel("192.168.1.10/24") { Other = "192.168.1.51" };
        Assert.Equal("192.168.1.51 is on this subnet.", calculator.OtherVerdict);

        calculator.Other = "192.168.2.51";
        Assert.Contains("NOT on", calculator.OtherVerdict, StringComparison.Ordinal);

        calculator.Other = "192.168.1.255";
        Assert.Contains("broadcast address", calculator.OtherVerdict, StringComparison.Ordinal);
    }

    [Fact]
    public void ShowsTheProblemRatherThanStaleAnswers()
    {
        var calculator = new SubnetCalculatorViewModel("192.168.1.10/24");
        Assert.Equal("192.168.1.0/24", calculator.Network);

        calculator.Input = "192.168.1.10/40";

        Assert.NotNull(calculator.Problem);
        Assert.Null(calculator.Network);
    }

    /// <summary>A first read of this PC's counters is a total, and says to read again.</summary>
    [Fact]
    public void AFirstReadOfThisPcsCountersIsATotal()
    {
        string text = Counters(errors: 4).Describe();

        Assert.Contains("4 received with errors", text, StringComparison.Ordinal);
        Assert.Contains("read again", text, StringComparison.Ordinal);
    }

    /// <summary>Errors still being counted on this PC's own adapter point at this PC's cable.</summary>
    [Fact]
    public void ErrorsThatMovedBetweenTwoReadsPointAtThisPcsOwnCable()
    {
        NicCounters first = Counters(errors: 4);
        NicCounters second = Counters(errors: 10) with { ReadUtc = first.ReadUtc.AddSeconds(30) };

        Assert.True(second.HasNewErrorsSince(first));
        Assert.Contains("6 error(s)", second.Describe(first), StringComparison.Ordinal);
        Assert.Contains("patch lead", second.Describe(first), StringComparison.Ordinal);

        NicCounters quiet = first with { ReadUtc = first.ReadUtc.AddSeconds(30) };
        Assert.False(quiet.HasNewErrorsSince(first));
        Assert.Contains("clean", quiet.Describe(first), StringComparison.Ordinal);
    }

    private static NicCounters Counters(long errors) => new()
    {
        Index = 12,
        Name = "Ethernet",
        ReadUtc = new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero),
        ReceivedWithErrors = errors,
        UnicastReceived = 1000,
    };
}
