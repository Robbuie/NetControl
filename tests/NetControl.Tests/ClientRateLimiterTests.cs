using NetControl.Core;
using NetControl.Core.Dhcp;
using Xunit;

namespace NetControl.Tests;

public sealed class ClientRateLimiterTests
{
    private static readonly MacAddress Loud = MacAddress.Parse("00:00:BC:00:00:01");
    private static readonly MacAddress Quiet = MacAddress.Parse("00:00:BC:00:00:02");

    [Fact]
    public void AllowsTheAllowanceThenCountsTheRest()
    {
        var time = new TestTimeProvider();
        var limiter = new ClientRateLimiter(3, time);

        bool[] results = [.. Enumerable.Range(0, 5).Select(_ => limiter.TryTake(Loud))];

        Assert.Equal([true, true, true, false, false], results);
        Assert.True(limiter.TryTake(Quiet));
        Assert.Equal((Loud, 2L), Assert.Single(limiter.DrainSuppressed()));
        Assert.Empty(limiter.DrainSuppressed());
    }

    [Fact]
    public void ANewSecondIsANewAllowance()
    {
        var time = new TestTimeProvider();
        var limiter = new ClientRateLimiter(1, time);

        Assert.True(limiter.TryTake(Loud));
        Assert.False(limiter.TryTake(Loud));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(limiter.TryTake(Loud));
    }

    [Fact]
    public void AFloodOfSpoofedMacsCostsAFixedAmountOfMemory()
    {
        var time = new TestTimeProvider();
        var limiter = new ClientRateLimiter(1, time);

        for (int i = 0; i < ClientRateLimiter.MaxClients + 500; i++)
        {
            byte[] bytes = [0x02, 0, (byte)(i >> 16), (byte)(i >> 8), (byte)i, 1];
            limiter.TryTake(new MacAddress(bytes));
        }

        Assert.True(limiter.Tracked <= ClientRateLimiter.MaxClients);
    }
}
