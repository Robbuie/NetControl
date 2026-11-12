using System.Net;
using NetControl.Core;
using NetControl.Core.Analysis;
using NetControl.Core.Interfaces;
using NetControl.Core.Reachability;
using NetControl.Core.Safety;
using Xunit;

namespace NetControl.Tests;

public sealed class TroubleshooterTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NoAdapterIsTheFirstProblem()
    {
        IReadOnlyList<NetworkFinding> findings = Troubleshooter.Assess(new TroubleshootInputs());

        Assert.Equal(FindingSeverity.Error, findings[0].Severity);
        Assert.Contains("No adapter", findings[0].Title, StringComparison.Ordinal);
    }

    [Fact]
    public void ALinkThatIsDownSendsYouToTheCable()
    {
        IReadOnlyList<NetworkFinding> findings = Troubleshooter.Assess(new TroubleshootInputs { Adapter = Nics.Down() });

        NetworkFinding first = findings[0];
        Assert.Equal("Cable", first.Area);
        Assert.Equal(FindingSeverity.Error, first.Severity);
    }

    [Fact]
    public void ErrorsCountedOnThisPcsPortAreAnError()
    {
        IReadOnlyList<NetworkFinding> findings = Troubleshooter.Assess(new TroubleshootInputs
        {
            Adapter = Nics.Healthy(),
            CountersBefore = Counters(At, errors: 10),
            CountersAfter = Counters(At.AddSeconds(3), errors: 25),
        });

        Assert.Contains(findings, f => f.Severity == FindingSeverity.Error && f.Area == "Cable" && f.Title.Contains("15 error", StringComparison.Ordinal));
    }

    [Fact]
    public void ABroadcastFloodSeenOnThisPcsCountersIsCalledAStorm()
    {
        IReadOnlyList<NetworkFinding> findings = Troubleshooter.Assess(new TroubleshootInputs
        {
            Adapter = Nics.Healthy(),
            CountersBefore = Counters(At, nonUnicast: 0),
            CountersAfter = Counters(At.AddSeconds(3), nonUnicast: 9000),
        });

        Assert.Contains(findings, f => f.Severity == FindingSeverity.Error && f.Area == "Traffic");
    }

    [Fact]
    public void APlanOnASubnetThisPcIsNotOnIsSaid()
    {
        IReadOnlyList<NetworkFinding> findings = Troubleshooter.Assess(new TroubleshootInputs
        {
            Adapter = Nics.Healthy(),
            Plan = [new PlannedAddress("Drive", IPAddress.Parse("120.15.106.51"), IPAddress.Parse("255.255.255.0"), null)],
        });

        Assert.Contains(findings, f => f.Severity == FindingSeverity.Warn && f.Title.Contains("not on any of the plan's subnets", StringComparison.Ordinal));
    }

    [Fact]
    public void AnotherAdapterOnTheSameSubnetIsAWarning()
    {
        NicInfo wifi = Nics.Healthy(index: 20, address: "192.168.1.77") with { Name = "Wi-Fi" };

        IReadOnlyList<NetworkFinding> findings = Troubleshooter.Assess(new TroubleshootInputs
        {
            Adapter = Nics.Healthy(),
            AllAdapters = [Nics.Healthy(), wifi],
        });

        Assert.Contains(findings, f => f.Title.Contains("Wi-Fi", StringComparison.Ordinal) && f.Severity == FindingSeverity.Warn);
    }

    [Fact]
    public void APlannedAddressHeldByADifferentMacInTheArpTableIsAnError()
    {
        IReadOnlyList<NetworkFinding> findings = Troubleshooter.Assess(new TroubleshootInputs
        {
            Adapter = Nics.Healthy(),
            Plan = [new PlannedAddress("Drive 3", IPAddress.Parse("192.168.1.51"), null, MacAddress.Parse("00:00:BC:11:22:33"))],
            Arp = [new ArpEntry(IPAddress.Parse("192.168.1.51"), MacAddress.Parse("00:0E:8C:44:55:66"), 12, ArpEntryType.Dynamic)],
        });

        Assert.Contains(findings, f => f.Severity == FindingSeverity.Error && f.Title.Contains("different device", StringComparison.Ordinal));
    }

    [Fact]
    public void SilentPlannedDevicesAreANoteNotAProblem()
    {
        var device = new PlannedAddress("Drive 3", IPAddress.Parse("192.168.1.51"), null, null);
        var other = new PlannedAddress("Drive 4", IPAddress.Parse("192.168.1.52"), null, null);

        IReadOnlyList<NetworkFinding> findings = Troubleshooter.Assess(new TroubleshootInputs
        {
            Adapter = Nics.Healthy(),
            ActiveChecksRan = true,
            PlanPings =
            [
                (device, new PingOutcome { Address = device.Address!, Status = PingStatus.TimedOut }),
                (other, new PingOutcome { Address = other.Address!, Status = PingStatus.Replied }),
            ],
        });

        NetworkFinding silent = Assert.Single(findings, f => f.Title.Contains("did not answer", StringComparison.Ordinal));
        Assert.Equal(FindingSeverity.Info, silent.Severity);
        Assert.Equal("No problems found", findings[0].Title);
    }

    [Fact]
    public void AHealthyRunSaysSoFirst()
    {
        IReadOnlyList<NetworkFinding> findings = Troubleshooter.Assess(new TroubleshootInputs
        {
            Adapter = Nics.Healthy(),
            CountersBefore = Counters(At),
            CountersAfter = Counters(At.AddSeconds(3)),
            ActiveChecksRan = true,
        });

        Assert.Equal("No problems found", findings[0].Title);
        Assert.DoesNotContain(findings, f => f.Severity >= FindingSeverity.Warn);
    }

    [Fact]
    public async Task TheRunnerSkipsPingsInListenOnlyModeAndSaysWhy()
    {
        var pinger = new FakePinger();
        var guard = new NetworkGuard { ListenOnly = true };
        var runner = new TroubleshootRunner(pinger, new FakeArpLookup(), guard)
        {
            ReadPort = port => PortConflictReport.Unknown(port),
            ReadFirewall = port => FirewallStatus.Unknown(port, "test"),
            ReadCounters = _ => null,
            ReadOwnDuplicate = _ => false,
        };

        (TroubleshootInputs inputs, IReadOnlyList<NetworkFinding> findings) = await runner.RunAsync(new TroubleshootRequest
        {
            Adapter = Nics.Healthy(),
            Plan = [new PlannedAddress("Drive", IPAddress.Parse("192.168.1.51"), null, null)],
            IncludeActiveChecks = true,
            CounterWindow = TimeSpan.Zero,
        });

        Assert.Empty(pinger.Asked);
        Assert.False(inputs.ActiveChecksRan);
        Assert.Contains(findings, f => f.Title.Contains("listen-only", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheRunnerPingsOnlyPlannedDevicesOnThisPcsSubnetAndNeverItself()
    {
        var pinger = new FakePinger { Delay = TimeSpan.Zero }.Answers("192.168.1.51", PingStatus.Replied);
        var runner = new TroubleshootRunner(pinger, new FakeArpLookup(), new NetworkGuard())
        {
            ReadPort = port => PortConflictReport.Unknown(port),
            ReadFirewall = port => FirewallStatus.Unknown(port, "test"),
            ReadCounters = _ => null,
            ReadOwnDuplicate = _ => false,
        };

        (TroubleshootInputs inputs, _) = await runner.RunAsync(new TroubleshootRequest
        {
            Adapter = Nics.Healthy(),
            Plan =
            [
                new PlannedAddress("Drive", IPAddress.Parse("192.168.1.51"), null, null),
                new PlannedAddress("Elsewhere", IPAddress.Parse("10.0.0.5"), null, null),
                new PlannedAddress("Me", IPAddress.Parse("192.168.1.10"), null, null),
                new PlannedAddress("Broadcast", IPAddress.Parse("192.168.1.255"), null, null),
            ],
            IncludeActiveChecks = true,
            CounterWindow = TimeSpan.Zero,
        });

        Assert.True(inputs.ActiveChecksRan);
        Assert.Equal([IPAddress.Parse("192.168.1.51")], pinger.Asked.Distinct());
    }

    private static NicCounters Counters(DateTimeOffset at, long errors = 0, long nonUnicast = 0) => new()
    {
        Index = 12,
        Name = "Ethernet",
        ReadUtc = at,
        ReceivedWithErrors = errors,
        NonUnicastReceived = nonUnicast,
    };
}
