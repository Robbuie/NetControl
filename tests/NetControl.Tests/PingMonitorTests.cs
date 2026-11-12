using System.Net;
using NetControl.Core.Analysis;
using NetControl.Core.Reachability;
using Xunit;

namespace NetControl.Tests;

public sealed class PingMonitorTests
{
    [Fact]
    public async Task CountsLossLatencyAndOneOutagePerRunOfLostEchoes()
    {
        var pinger = new FakePinger { Delay = TimeSpan.Zero }.Answers(
            "192.168.1.51",
            PingStatus.Replied, PingStatus.TimedOut, PingStatus.TimedOut, PingStatus.TimedOut, PingStatus.TimedOut,
            PingStatus.Replied, PingStatus.Replied);
        var time = new TestTimeProvider();
        await using var monitor = new PingMonitor(pinger, time);
        var changes = new List<bool>();
        monitor.StateChanged += (_, e) => changes.Add(e.Down);

        Assert.Null(monitor.Add(IPAddress.Parse("192.168.1.51"), "Drive 3"));

        for (int i = 0; i < 7; i++)
        {
            await monitor.RoundAsync();
            time.Advance(TimeSpan.FromSeconds(1));
        }

        PingTargetStats stats = Assert.Single(monitor.Snapshot());
        Assert.Equal(7, stats.Sent);
        Assert.Equal(3, stats.Received);
        Assert.Equal(1, stats.Outages);
        Assert.False(stats.IsDown);
        Assert.Equal(TimeSpan.FromSeconds(4), stats.LongestOutage);
        Assert.Equal([true, false], changes);
        Assert.Equal(7, stats.History.Count);
        Assert.Null(stats.History[1]);
        Assert.Equal(2, stats.AverageMs);
    }

    [Fact]
    public async Task OneOrTwoLostEchoesAreDropsNotAnOutage()
    {
        var pinger = new FakePinger { Delay = TimeSpan.Zero }.Answers(
            "192.168.1.51", PingStatus.Replied, PingStatus.TimedOut, PingStatus.TimedOut, PingStatus.Replied);
        await using var monitor = new PingMonitor(pinger, new TestTimeProvider());
        monitor.Add(IPAddress.Parse("192.168.1.51"));

        for (int i = 0; i < 4; i++)
        {
            await monitor.RoundAsync();
        }

        Assert.Equal(0, Assert.Single(monitor.Snapshot()).Outages);
    }

    [Fact]
    public void RefusesAnythingThatIsNotOneHostAndCapsTheList()
    {
        var monitor = new PingMonitor(new FakePinger());

        Assert.NotNull(monitor.Add(IPAddress.Broadcast));
        Assert.NotNull(monitor.Add(IPAddress.Parse("192.168.1.255"), mask: IPAddress.Parse("255.255.255.0")));
        Assert.NotNull(monitor.Add(IPAddress.Parse("224.0.0.1")));

        for (int i = 1; i <= PingMonitor.MaxTargets; i++)
        {
            Assert.Null(monitor.Add(IPAddress.Parse($"10.0.{i / 250}.{(i % 250) + 1}")));
        }

        Assert.NotNull(monitor.Add(IPAddress.Parse("10.9.9.9")));
        Assert.NotNull(monitor.Add(IPAddress.Parse("10.0.0.2")));
        Assert.Equal(PingMonitor.MaxTargets, monitor.Count);
    }

    [Fact]
    public async Task NeverPingsMoreThanItsInFlightLimitAtOnce()
    {
        var pinger = new FakePinger { Delay = TimeSpan.FromMilliseconds(20) };
        await using var monitor = new PingMonitor(pinger);

        for (int i = 1; i <= 30; i++)
        {
            monitor.Add(IPAddress.Parse($"10.0.0.{i}"));
        }

        await monitor.RoundAsync();

        Assert.True(pinger.MaxConcurrent <= PingMonitor.MaxInFlight);
        Assert.Equal(30, pinger.Asked.Count);
    }
}
