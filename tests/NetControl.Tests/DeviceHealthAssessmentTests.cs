using System.Net;
using NetControl.Core.Cip;
using NetControl.Core.DeviceHealth;
using NetControl.Core.Persistence;
using Xunit;

namespace NetControl.Tests;

/// <summary>
/// What a diagnostics read means. Pure: every report here is built by hand, so each rule is tested
/// on its own and none needs a device.
///
/// <para>The rule these keep coming back to: <b>a counter is graded by whether it is still
/// moving.</b> A total since power-up is reported as a total and the user is told to read again; a
/// second read that shows it moved is the Warn. The exceptions are the counters that should never be
/// anything but zero on a switched network.</para>
/// </summary>
public class DeviceHealthAssessmentTests
{
    private static readonly IPAddress Address = IPAddress.Parse("192.168.1.51");
    private static readonly DateTimeOffset Then = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Link up, 100 full, negotiated: 0x01 | 0x02 | (3 &lt;&lt; 2).</summary>
    private const uint Healthy = 0x0F;

    [Fact]
    public void AHealthyStaticDeviceHasNothingWorthAWarning()
    {
        DeviceHealthResult result = DeviceHealthAssessment.Assess(Report(Port(Healthy)) with
        {
            Method = ConfigMethod.Static,
        });

        Assert.Empty(result.Findings);
        Assert.Equal(EventSeverity.Info, result.Severity);
        Assert.Contains("nothing wrong found", result.Summary, StringComparison.Ordinal);
    }

    /// <summary>Still a BOOTP client is worth knowing - it asks again at the next power cycle - but it is not a fault.</summary>
    [Fact]
    public void SaysADeviceIsStillABootpClientWithoutCallingItAFault()
    {
        DeviceHealthResult result = DeviceHealthAssessment.Assess(Report(Port(Healthy)) with
        {
            Method = ConfigMethod.Bootp,
        });

        HealthFinding finding = Assert.Single(result.Findings);
        Assert.Equal(EventSeverity.Info, finding.Severity);
        Assert.Contains("BOOTP client", finding.Message, StringComparison.Ordinal);
    }

    /// <summary>Half duplex on link up, with late collisions: the duplex mismatch, both halves of it.</summary>
    [Fact]
    public void NamesTheDuplexMismatchFromTheFlagsAndTheCounters()
    {
        uint halfDuplexDefaulted = 0x01 | (2u << 2);

        DeviceHealthResult result = DeviceHealthAssessment.Assess(Report(Port(
            halfDuplexDefaulted,
            media: new MediaCounters { LateCollisions = 37, FcsErrors = 12 })));

        Assert.Contains(result.Findings, f => f.Severity == EventSeverity.Warn
            && f.Message.Contains("half duplex", StringComparison.Ordinal));
        Assert.Contains(result.Findings, f => f.Severity == EventSeverity.Warn
            && f.Message.Contains("could not negotiate duplex", StringComparison.Ordinal));

        // Late collisions are a Warn on a first read: they should never happen on a switched link.
        Assert.Contains(result.Findings, f => f.Severity == EventSeverity.Warn
            && f.Message.Contains("37 late collision", StringComparison.Ordinal));
    }

    /// <summary>Any collision at all on a full-duplex port says the far end is half duplex.</summary>
    [Fact]
    public void ACollisionOnAFullDuplexLinkIsAWarnOnSight()
    {
        DeviceHealthResult result = DeviceHealthAssessment.Assess(Report(Port(
            Healthy, media: new MediaCounters { SingleCollisions = 5 })));

        HealthFinding finding = Assert.Single(result.Findings);
        Assert.Equal(EventSeverity.Warn, finding.Severity);
        Assert.Contains("full-duplex", finding.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A first read sees a total since power-up, and a handful of FCS errors from the day the cable
    /// was plugged in is not a fault. Info, and an instruction to read again.
    /// </summary>
    [Fact]
    public void ReportsAnErrorTotalOnAFirstReadAsInfoAndSaysToReadAgain()
    {
        DeviceHealthResult result = DeviceHealthAssessment.Assess(Report(Port(
            Healthy, media: new MediaCounters { FcsErrors = 12 })));

        HealthFinding finding = Assert.Single(result.Findings);
        Assert.Equal(EventSeverity.Info, finding.Severity);
        Assert.Contains("12 frame(s) arrived damaged", finding.Message, StringComparison.Ordinal);
        Assert.Contains("read again", finding.Message, StringComparison.Ordinal);
    }

    /// <summary>Read again a minute later and it has moved: that is happening now, and it is a Warn.</summary>
    [Fact]
    public void ACounterThatMovedBetweenTwoReadsIsAWarnWithTheRate()
    {
        DeviceHealthReport before = Report(Port(Healthy, media: new MediaCounters { FcsErrors = 12 }));
        DeviceHealthReport after = Report(Port(Healthy, media: new MediaCounters { FcsErrors = 20 })) with
        {
            ReadUtc = Then.AddSeconds(60),
        };

        DeviceHealthResult result = DeviceHealthAssessment.Assess(after, before);

        HealthFinding finding = Assert.Single(result.Findings);
        Assert.Equal(EventSeverity.Warn, finding.Severity);
        Assert.Contains("8 frame(s) arrived damaged", finding.Message, StringComparison.Ordinal);
        Assert.Contains("in the last 60 s", finding.Message, StringComparison.Ordinal);
        Assert.Contains("still counting", finding.Message, StringComparison.Ordinal);
    }

    /// <summary>And if it has not moved, there is nothing to say about it at all.</summary>
    [Fact]
    public void ACounterThatDidNotMoveSaysNothing()
    {
        DeviceHealthReport before = Report(Port(Healthy, media: new MediaCounters { FcsErrors = 12 }));
        DeviceHealthReport after = before with { ReadUtc = Then.AddSeconds(60) };

        Assert.Empty(DeviceHealthAssessment.Assess(after, before).Findings);
    }

    /// <summary>
    /// Counters lower than last time mean the device restarted or something cleared them. Said, and
    /// nothing compared - subtracting across a reset would invent numbers.
    /// </summary>
    [Fact]
    public void CountersThatWentBackwardsAreSaidAndNotCompared()
    {
        DeviceHealthReport before = Report(Port(Healthy, media: new MediaCounters { FcsErrors = 500 }));
        DeviceHealthReport after = Report(Port(Healthy, media: new MediaCounters { FcsErrors = 3 })) with
        {
            ReadUtc = Then.AddSeconds(60),
        };

        DeviceHealthResult result = DeviceHealthAssessment.Assess(after, before);

        Assert.Contains(result.Findings, f => f.Message.Contains("went backwards", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Findings, f => f.Severity != EventSeverity.Info);
    }

    /// <summary>
    /// A different serial at the same address is a different module - somebody swapped it between
    /// the reads. Its counters are not the old one's counters, and comparing them would be fiction.
    /// </summary>
    [Fact]
    public void DoesNotCompareTheCountersOfTwoDifferentDevices()
    {
        DeviceHealthReport before = Report(Port(Healthy, media: new MediaCounters { FcsErrors = 1 }));
        DeviceHealthReport after = Report(Port(Healthy, media: new MediaCounters { FcsErrors = 900 })) with
        {
            ReadUtc = Then.AddSeconds(60),
            SerialNumber = 0xBADC0DE,
        };

        DeviceHealthResult result = DeviceHealthAssessment.Assess(after, before);

        Assert.Contains(result.Findings, f => f.Message.Contains("not the device", StringComparison.Ordinal));
        // The total is still reported - as a total, Info, never as "in the last N s".
        Assert.DoesNotContain(result.Findings, f => f.Message.Contains("in the last", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Findings, f => f.Severity == EventSeverity.Warn);
    }

    [Fact]
    public void AHardwareFaultOnThePortIsAnError()
    {
        DeviceHealthResult result = DeviceHealthAssessment.Assess(Report(Port(Healthy | 0x40)));

        Assert.Equal(EventSeverity.Error, result.Severity);
        Assert.Contains("hardware", result.Findings[0].Message, StringComparison.Ordinal);
    }

    /// <summary>The device's own address conflict detection has seen somebody else on its address.</summary>
    [Fact]
    public void AnAddressConflictTheDeviceDetectedIsAnError()
    {
        DeviceHealthResult result = DeviceHealthAssessment.Assess(Report(Port(Healthy)) with
        {
            TcpIpStatus = new TcpIpStatus(0x01 | 0x40),
        });

        Assert.Equal(EventSeverity.Error, result.Severity);
        Assert.Contains("Address conflict detected", result.Findings[0].Message, StringComparison.Ordinal);
    }

    /// <summary>Written and not applied: the bit that explains an unverified Set static.</summary>
    [Fact]
    public void AConfigurationWaitingForAResetIsAWarn()
    {
        DeviceHealthResult result = DeviceHealthAssessment.Assess(Report(Port(Healthy)) with
        {
            TcpIpStatus = new TcpIpStatus(0x01 | 0x20),
        });

        HealthFinding finding = Assert.Single(result.Findings);
        Assert.Equal(EventSeverity.Warn, finding.Severity);
        Assert.Contains("waiting for a reset", finding.Message, StringComparison.Ordinal);
    }

    /// <summary>Faults sort first, whatever order the rules found them in.</summary>
    [Fact]
    public void PutsTheWorstFindingFirst()
    {
        DeviceHealthResult result = DeviceHealthAssessment.Assess(Report(Port(0x01 | (3u << 2))) with
        {
            Method = ConfigMethod.Dhcp,
            Status = new IdentityStatus(0x0400),
        });

        Assert.Equal(EventSeverity.Error, result.Findings[0].Severity);
        Assert.Contains("major recoverable fault", result.Findings[0].Message, StringComparison.Ordinal);
        Assert.Equal(EventSeverity.Info, result.Findings[^1].Severity);
    }

    /// <summary>An unused second port is normal; it is mentioned, quietly.</summary>
    [Fact]
    public void AnUnusedSecondPortIsInfo()
    {
        DeviceHealthResult result = DeviceHealthAssessment.Assess(Report(Port(Healthy), Port(0, instance: 2)));

        HealthFinding finding = Assert.Single(result.Findings);
        Assert.Equal(EventSeverity.Info, finding.Severity);
        Assert.Equal((ushort)2, finding.Port);
        Assert.Contains("Port 2 has no link", finding.Message, StringComparison.Ordinal);
    }

    /// <summary>Answering at one address while saying it holds another is worth a sentence.</summary>
    [Fact]
    public void NoticesADeviceThatSaysItHoldsADifferentAddress()
    {
        DeviceHealthResult result = DeviceHealthAssessment.Assess(Report(Port(Healthy)) with
        {
            Configuration = new InterfaceConfig
            {
                Ip = IPAddress.Parse("192.168.1.99"),
                Mask = IPAddress.Parse("255.255.255.0"),
            },
        });

        HealthFinding finding = Assert.Single(result.Findings);
        Assert.Equal(EventSeverity.Warn, finding.Severity);
        Assert.Contains("192.168.1.99", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TenMegabitIsAWarn()
    {
        DeviceHealthResult result = DeviceHealthAssessment.Assess(Report(Port(Healthy, speed: 10)));

        HealthFinding finding = Assert.Single(result.Findings);
        Assert.Contains("10 Mb/s", finding.Message, StringComparison.Ordinal);
    }

    private static DeviceHealthReport Report(params LinkPort[] ports) => new()
    {
        Address = Address,
        ReadUtc = Then,
        ProductName = "1734-AENT/B",
        VendorId = 1,
        ProductCode = 167,
        SerialNumber = 0x00C0FFEE,
        Ports = ports,
    };

    private static LinkPort Port(
        uint flags, ushort instance = 1, uint speed = 100, MediaCounters? media = null, InterfaceCounters? counters = null) =>
        new()
        {
            Instance = instance,
            Flags = new LinkFlags(flags),
            SpeedMbps = (flags & 0x01) != 0 ? speed : 0,
            Media = media,
            Interface = counters,
        };
}
