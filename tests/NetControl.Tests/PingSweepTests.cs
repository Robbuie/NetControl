using System.Net;
using NetControl.Core.Persistence;
using NetControl.Core.Reachability;
using Xunit;

namespace NetControl.Tests;

/// <summary>
/// The ping sweep: what it refuses before sending anything, how gently it goes about the rest, and
/// how it words what it found. Every test uses <see cref="FakePinger"/> except the last, which pings
/// loopback - the only thing a test here is ever allowed to put an echo request on.
/// </summary>
public class PingSweepTests
{
    private static readonly PingSweepOptions Quick = new()
    {
        Timeout = TimeSpan.FromMilliseconds(100),
        PauseBetween = TimeSpan.Zero,
    };

    [Theory]
    [InlineData("255.255.255.255")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("239.192.1.1")]
    [InlineData("250.1.1.1")]
    public void RefusesAnAddressThatIsNeverOneHost(string address)
    {
        Assert.False(UnicastTarget.TryCheck(IPAddress.Parse(address), mask: null, out string? problem));
        Assert.NotNull(problem);
    }

    /// <summary>With the mask known, the subnet's own broadcast and network addresses are refused too.</summary>
    [Theory]
    [InlineData("192.168.1.255", "255.255.255.0")]
    [InlineData("192.168.1.0", "255.255.255.0")]
    [InlineData("10.0.0.63", "255.255.255.192")]
    public void RefusesTheDirectedBroadcastAndNetworkAddressWhenTheMaskIsKnown(string address, string mask)
    {
        Assert.False(UnicastTarget.TryCheck(IPAddress.Parse(address), IPAddress.Parse(mask), out _));

        // Without the mask nothing says it is not a host, so it is allowed - the scan's rule exactly.
        Assert.True(UnicastTarget.TryCheck(IPAddress.Parse(address), mask: null, out _));
    }

    /// <summary>A /31 has no broadcast address to refuse (RFC 3021); both ends are hosts.</summary>
    [Fact]
    public void AllowsBothEndsOfAPointToPointSubnet()
    {
        IPAddress mask = IPAddress.Parse("255.255.255.254");

        Assert.True(UnicastTarget.TryCheck(IPAddress.Parse("10.0.0.0"), mask, out _));
        Assert.True(UnicastTarget.TryCheck(IPAddress.Parse("10.0.0.1"), mask, out _));
    }

    /// <summary>Loopback is allowed: it is where every test points, and it is on nobody's plant network.</summary>
    [Fact]
    public void AllowsLoopbackAndAnOrdinaryHost()
    {
        Assert.True(UnicastTarget.TryCheck(IPAddress.Loopback, null, out _));
        Assert.True(UnicastTarget.TryCheck(IPAddress.Parse("192.168.1.51"), IPAddress.Parse("255.255.255.0"), out _));
    }

    /// <summary>
    /// One bad address refuses the whole sweep with <b>nothing sent</b> - not even to the good ones
    /// that came before it in the list. A planned broadcast address is a mistake in the plan, and
    /// finding out by having every device answer is the wrong way to learn it.
    /// </summary>
    [Fact]
    public async Task OneBroadcastInTheListRefusesTheSweepBeforeAnythingIsSent()
    {
        var pinger = new FakePinger().Answers("192.168.1.51", PingStatus.Replied);
        var sweep = new PingSweep(pinger, Quick);

        await Assert.ThrowsAsync<ReachabilityException>(() => sweep.RunAsync(
        [
            new PingTarget(IPAddress.Parse("192.168.1.51"), IPAddress.Parse("255.255.255.0")),
            new PingTarget(IPAddress.Parse("192.168.1.255"), IPAddress.Parse("255.255.255.0")),
        ]));

        Assert.Empty(pinger.Asked);
    }

    /// <summary>The first echo is often lost while ARP resolves, so a silent address is asked twice.</summary>
    [Fact]
    public async Task AsksASilentAddressAgainAndStopsAtTheFirstReply()
    {
        var pinger = new FakePinger()
            .Answers("192.168.1.51", PingStatus.TimedOut, PingStatus.Replied)
            .Answers("192.168.1.52", PingStatus.Replied);

        PingSweepResult result = await new PingSweep(pinger, Quick).RunAsync(
            [new PingTarget(IPAddress.Parse("192.168.1.51")), new PingTarget(IPAddress.Parse("192.168.1.52"))]);

        Assert.Equal(2, result.Replied);
        Assert.Equal(2, result.Outcomes[0].Attempts);
        Assert.Equal(1, result.Outcomes[1].Attempts);
        Assert.Equal(3, pinger.Asked.Count);
    }

    /// <summary>"No route" is not asked about again: a second attempt will not grow one.</summary>
    [Fact]
    public async Task DoesNotRetryAnAddressWithNoRoute()
    {
        var pinger = new FakePinger().Answers("10.9.9.9", PingStatus.Unreachable, PingStatus.Replied);

        PingSweepResult result = await new PingSweep(pinger, Quick).RunAsync([new PingTarget(IPAddress.Parse("10.9.9.9"))]);

        Assert.Equal(PingStatus.Unreachable, result.Outcomes[0].Status);
        Assert.Single(pinger.Asked);
    }

    /// <summary>Gentle: never more in flight than the option allows, however long the list.</summary>
    [Fact]
    public async Task NeverHasMoreInFlightThanAllowed()
    {
        var pinger = new FakePinger { Delay = TimeSpan.FromMilliseconds(40) };
        var options = Quick with { MaxInFlight = 3, Attempts = 1 };

        List<PingTarget> targets = [.. Enumerable.Range(1, 12)
            .Select(n => new PingTarget(IPAddress.Parse($"192.168.1.{n}")))];

        await new PingSweep(pinger, options).RunAsync(targets);

        Assert.Equal(12, pinger.Asked.Count);
        Assert.InRange(pinger.MaxConcurrent, 1, 3);
    }

    /// <summary>Results come back in the order asked, whatever order the answers arrived in.</summary>
    [Fact]
    public async Task KeepsTheOrderItWasGivenAndPingsADuplicateOnce()
    {
        var pinger = new FakePinger()
            .Answers("192.168.1.3", PingStatus.Replied)
            .Answers("192.168.1.1", PingStatus.Replied);

        PingSweepResult result = await new PingSweep(pinger, Quick with { Attempts = 1 }).RunAsync(
        [
            new PingTarget(IPAddress.Parse("192.168.1.3")),
            new PingTarget(IPAddress.Parse("192.168.1.1")),
            new PingTarget(IPAddress.Parse("192.168.1.3")),
        ]);

        Assert.Equal(2, result.Outcomes.Count);
        Assert.Equal(IPAddress.Parse("192.168.1.3"), result.Outcomes[0].Address);
        Assert.Equal(IPAddress.Parse("192.168.1.1"), result.Outcomes[1].Address);
    }

    /// <summary>
    /// Silence from some devices is ordinary - plenty ignore ping - so it is Info. Nothing answering
    /// at all says more about the laptop than the devices, and is a Warn that says so.
    /// </summary>
    [Fact]
    public async Task GradesTotalSilenceAsAProblemWithThisPcRatherThanTheDevices()
    {
        var some = new FakePinger().Answers("192.168.1.1", PingStatus.Replied);
        PingSweepResult partly = await new PingSweep(some, Quick with { Attempts = 1 }).RunAsync(
            [new PingTarget(IPAddress.Parse("192.168.1.1")), new PingTarget(IPAddress.Parse("192.168.1.2"))]);

        Assert.Equal(EventSeverity.Info, partly.Severity);
        Assert.Contains("1 answered, 1 did not", partly.Summary, StringComparison.Ordinal);

        PingSweepResult none = await new PingSweep(new FakePinger(), Quick with { Attempts = 1 }).RunAsync(
            [new PingTarget(IPAddress.Parse("192.168.1.1")), new PingTarget(IPAddress.Parse("192.168.1.2"))]);

        Assert.Equal(EventSeverity.Warn, none.Severity);
        Assert.Contains("check the adapter", none.Summary, StringComparison.Ordinal);
    }

    /// <summary>Every address raises progress exactly once, with a running count.</summary>
    [Fact]
    public async Task ReportsEachAddressAsItIsAnswered()
    {
        var sweep = new PingSweep(new FakePinger(), Quick with { Attempts = 1 });
        var seen = new List<int>();
        sweep.Progress += (_, e) =>
        {
            lock (seen)
            {
                seen.Add(e.Completed);
            }
        };

        await sweep.RunAsync(
            [new PingTarget(IPAddress.Parse("192.168.1.1")), new PingTarget(IPAddress.Parse("192.168.1.2"))]);

        Assert.Equal(new[] { 1, 2 }, seen.Order());
    }

    /// <summary>
    /// The real pinger, against loopback only. Windows answers an echo to 127.0.0.1 without
    /// administrator rights, which is the whole reason <see cref="IcmpPinger"/> needs none.
    /// </summary>
    [Fact]
    public async Task TheRealPingerGetsAnAnswerFromLoopback()
    {
        PingOutcome outcome = await new IcmpPinger().PingAsync(IPAddress.Loopback, TimeSpan.FromSeconds(2));

        Assert.Equal(PingStatus.Replied, outcome.Status);
        Assert.NotNull(outcome.RoundTrip);
    }
}
