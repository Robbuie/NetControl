using NetControl.Core.Interfaces;
using Xunit;

namespace NetControl.Tests;

public class FirewallCheckTests
{
    [Theory]
    [InlineData("*")]
    [InlineData("67")]
    [InlineData("67,68")]
    [InlineData("68,67")]
    [InlineData("60-70")]
    [InlineData("500,60-70,4500")]
    [InlineData(" 67 , 68 ")]
    public void RecognisesEveryFormThatCoversThePort(string localPorts)
    {
        Assert.True(FirewallCheck.PortMatches(localPorts, 67));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("68")]
    [InlineData("68-80")]
    [InlineData("RPC")]
    [InlineData("nonsense")]
    public void TreatsAnythingItCannotProveAsNotMatching(string? localPorts)
    {
        // Claiming a rule covers the port when it might not is the failure that costs an hour
        // on site, so an unparseable value is "no" rather than "probably".
        Assert.False(FirewallCheck.PortMatches(localPorts, 67));
    }

    [Fact]
    public void HandsBackACommandTheUserCanActuallyRun()
    {
        string command = FirewallCheck.BuildAddRuleCommand(67, @"C:\Tools\netcontrol.exe");

        Assert.Contains("localport=67", command, StringComparison.Ordinal);
        Assert.Contains("dir=in", command, StringComparison.Ordinal);
        Assert.Contains("action=allow", command, StringComparison.Ordinal);
        Assert.Contains(@"program=""C:\Tools\netcontrol.exe""", command, StringComparison.Ordinal);
    }

    [Fact]
    public void NeverThrowsAndNeverClaimsSuccessItCannotProve()
    {
        // Runs on the CI agent too, where the firewall COM component may be absent entirely.
        FirewallStatus status = FirewallCheck.Inspect(67);

        if (status.Verdict == FirewallVerdict.Unknown)
        {
            Assert.False(status.IsClear);
        }

        Assert.False(string.IsNullOrWhiteSpace(status.Summary));
    }
}
