using System.Net;
using NetControl.Core;
using NetControl.Core.Dhcp;
using Xunit;

namespace NetControl.Tests;

public class StaticMapPolicyTests
{
    private static readonly MacAddress PlannedMac = MacAddress.Parse(Frames.DeviceMac);

    private static DeviceAssignment Planned(string ip = "192.168.1.51") => new(
        PlannedMac, IPAddress.Parse(ip), IPAddress.Parse("255.255.255.0"), IPAddress.Parse("192.168.1.1"));

    private static BootpPacket Request(MacAddress? mac = null) => new()
    {
        Op = BootpPacket.OpBootRequest,
        ClientMac = mac ?? PlannedMac,
        TransactionId = 0xFC5D1465,
    };

    /// <summary>
    /// The rule the whole product rests on. A MAC nobody typed never gets an address, because a
    /// rogue DHCP server on a plant network is a genuinely serious incident.
    /// </summary>
    [Fact]
    public void NeverServesAMacThatIsNotInThePlan()
    {
        var policy = new StaticMapPolicy([Planned()]);

        AssignmentDecision decision = policy.Decide(Request(MacAddress.Parse("00:1D:9C:AA:BB:CC")), Nics.Healthy());

        Assert.False(decision.ShouldServe);
        Assert.Contains("not in the plan", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ServesAPlannedMacOnAHealthyAdapter()
    {
        var policy = new StaticMapPolicy([Planned()]);

        AssignmentDecision decision = policy.Decide(Request(), Nics.Healthy());

        Assert.True(decision.ShouldServe);
        Assert.Equal(IPAddress.Parse("192.168.1.51"), decision.Assignment!.Ip);
    }

    [Fact]
    public void WillNotGuessWhichAdapterToReplyFrom()
    {
        var policy = new StaticMapPolicy([Planned()]);

        AssignmentDecision decision = policy.Decide(Request(), arrivalNic: null);

        Assert.False(decision.ShouldServe);
    }

    [Fact]
    public void ExplainsExactlyWhyAnAdapterCannotServe()
    {
        var policy = new StaticMapPolicy([Planned()]);

        Assert.Contains("link", policy.Decide(Request(), Nics.Down()).Reason, StringComparison.Ordinal);
        Assert.Contains("no IPv4", policy.Decide(Request(), Nics.NoAddress()).Reason, StringComparison.Ordinal);
        Assert.Contains("APIPA", policy.Decide(Request(), Nics.Apipa()).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesAnAddressTheLaptopCouldNotThenReach()
    {
        var policy = new StaticMapPolicy([Planned("10.0.0.51")]);

        AssignmentDecision decision = policy.Decide(Request(), Nics.Healthy());

        Assert.False(decision.ShouldServe);
        Assert.Contains("unreachable", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AllowsACrossSubnetPlanWhenThatIsDeliberate()
    {
        var policy = new StaticMapPolicy([Planned("10.0.0.51")]) { RequireSameSubnet = false };

        Assert.True(policy.Decide(Request(), Nics.Healthy()).ShouldServe);
    }

    [Fact]
    public void IgnoresRequestsFromAddressesThatCannotBeADevice()
    {
        var policy = new StaticMapPolicy([Planned()]);

        Assert.False(policy.Decide(Request(MacAddress.Empty), Nics.Healthy()).ShouldServe);
        Assert.False(policy.Decide(Request(MacAddress.Parse("FF:FF:FF:FF:FF:FF")), Nics.Healthy()).ShouldServe);
    }

    [Fact]
    public void CanBeEditedWhileTheServerIsRunning()
    {
        var policy = new StaticMapPolicy();
        Assert.False(policy.Decide(Request(), Nics.Healthy()).ShouldServe);

        policy.Add(Planned());
        Assert.True(policy.Decide(Request(), Nics.Healthy()).ShouldServe);

        policy.Remove(PlannedMac);
        Assert.False(policy.Decide(Request(), Nics.Healthy()).ShouldServe);
    }
}
