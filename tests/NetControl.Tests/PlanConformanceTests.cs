using System.Net;
using NetControl.Core;
using NetControl.Core.Discovery;
using NetControl.Core.Enip;
using NetControl.Core.Persistence;
using Xunit;

namespace NetControl.Tests;

/// <summary>
/// What a scan has to say about the plan.
///
/// <para>This is the check the active scan was built for, and it is the one conflict check that
/// can be honest: a duplicate check reading only the plan clears an address a switch, an HMI or
/// last year's untracked device is already holding, because none of those were ever typed into
/// it.</para>
///
/// <para>Every case here is arranged rather than scanned - what happens on the wire is
/// <c>IdentityScannerTests</c>' job. These are about the conclusions.</para>
/// </summary>
public class PlanConformanceTests
{
    private static readonly MacAddress Drive = MacAddress.Parse("00:1D:9C:C7:B0:70");
    private static readonly MacAddress Hmi = MacAddress.Parse("00:0F:73:11:22:33");
    private static readonly MacAddress Stranger = MacAddress.Parse("00:80:F4:AA:BB:CC");

    /// <summary>
    /// The headline. Nothing else in the tool can find this: the plan does not know the HMI exists,
    /// and the scan does not know anybody planned that address.
    /// </summary>
    [Fact]
    public void FindsAPlannedAddressThatSomethingElseIsAlreadySittingOn()
    {
        PlanConformanceReport report = PlanConformance.Compare(
            [Planned(1, Drive, "192.168.1.51", "conveyor drive")],
            Scan(Answering("192.168.1.51", Hmi, "PanelView 800")));

        PlanFinding finding = Assert.Single(report.Findings);

        Assert.Equal(PlanFindingKind.AddressTakenByAnother, finding.Kind);
        Assert.Equal(EventSeverity.Error, finding.Severity);
        Assert.True(report.HasConflicts);

        // Both ends of the disagreement, and what serving the plan would do about it - which is
        // nothing, and that is the part somebody has to know before pressing Serve.
        Assert.Contains("conveyor drive", finding.Message, StringComparison.Ordinal);
        Assert.Contains("PanelView 800", finding.Message, StringComparison.Ordinal);
        Assert.Contains("BOOTP answers the MAC it was asked about", finding.Message, StringComparison.Ordinal);

        // It is about a plan row, so it can be selected and it can be attributed in the record.
        Assert.Equal(1, finding.DeviceId);
    }

    /// <summary>The cheap version of the CIP readback: the device is where the plan puts it.</summary>
    [Fact]
    public void ConfirmsAPlannedDeviceThatAnsweredAtItsPlannedAddress()
    {
        PlanConformanceReport report = PlanConformance.Compare(
            [Planned(1, Drive, "192.168.1.51", "conveyor drive")],
            Scan(Answering("192.168.1.51", Drive)));

        Assert.Equal(PlanFindingKind.Confirmed, Assert.Single(report.Findings).Kind);
        Assert.False(report.HasConflicts);
        Assert.Equal(1, report.ConfirmedCount);
        Assert.Equal(EventSeverity.Info, report.Severity);
    }

    /// <summary>
    /// <b>Silence is not evidence.</b> A planned device that did not answer produces nothing at
    /// all: it may be powered down, behind a switch this scan did not reach, or not built yet. A
    /// list that reported every absence as a problem is a list people learn to skip.
    /// </summary>
    [Fact]
    public void SaysNothingAboutAPlannedDeviceThatDidNotAnswer()
    {
        PlanConformanceReport report = PlanConformance.Compare(
            [Planned(1, Drive, "192.168.1.51"), Planned(2, Hmi, "192.168.1.20")],
            Scan());

        Assert.Empty(report.Findings);
        Assert.Equal(2, report.PlannedWithAddress);
        Assert.Equal(0, report.ConfirmedCount);
    }

    /// <summary>
    /// Two facts about one row, and both are reported. "Your device is not where you meant it to
    /// be" and "something else is where you meant it to be" are different problems with different
    /// fixes, and on a half-commissioned panel they are usually both true.
    /// </summary>
    [Fact]
    public void ReportsBothThatTheDeviceMovedAndThatItsAddressIsTaken()
    {
        PlanConformanceReport report = PlanConformance.Compare(
            [Planned(1, Drive, "192.168.1.51", "conveyor drive")],
            Scan(
                Answering("192.168.1.51", Hmi, "PanelView 800"),
                Answering("192.168.1.77", Drive)));

        Assert.Equal(2, report.Findings.Count);
        Assert.Contains(report.Findings, f => f.Kind == PlanFindingKind.AddressTakenByAnother);
        Assert.Contains(report.Findings, f => f.Kind == PlanFindingKind.PlannedDeviceElsewhere);

        // Worst first: the conflict is read before the observation.
        Assert.Equal(PlanFindingKind.AddressTakenByAnother, report.Findings[0].Kind);
    }

    /// <summary>The fault only a scan can find, tied to the row that planned the address.</summary>
    [Fact]
    public void ReportsTwoDevicesAnsweringOnAPlannedAddress()
    {
        PlanConformanceReport report = PlanConformance.Compare(
            [Planned(1, Drive, "192.168.1.51", "conveyor drive")],
            Scan(
                Answering("192.168.1.51", Hmi, "PanelView 800", contested: true),
                Answering("192.168.1.51", Stranger, "1783-BMS10CGP", contested: true)));

        PlanFinding finding = Assert.Single(report.Findings);

        Assert.Equal(PlanFindingKind.ContestedAddress, finding.Kind);
        Assert.Equal(EventSeverity.Error, finding.Severity);
        Assert.Contains("PanelView 800", finding.Message, StringComparison.Ordinal);
        Assert.Contains("1783-BMS10CGP", finding.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A contested address stays contested even when one of the two is the device that was planned
    /// there. Half-right is not right, and reporting it as a confirmation would be the worst
    /// possible answer - a green row over a segment with two devices fighting on one address.
    /// </summary>
    [Fact]
    public void DoesNotConfirmAnAddressTwoDevicesAreAnsweringOn()
    {
        PlanConformanceReport report = PlanConformance.Compare(
            [Planned(1, Drive, "192.168.1.51")],
            Scan(
                Answering("192.168.1.51", Drive, contested: true),
                Answering("192.168.1.51", Hmi, "PanelView 800", contested: true)));

        Assert.Equal(PlanFindingKind.ContestedAddress, Assert.Single(report.Findings).Kind);
        Assert.Equal(0, report.ConfirmedCount);
    }

    /// <summary>
    /// Something answered and there is no hardware address to say whether it is the planned device
    /// or a different one. That is neither a confirmation nor a conflict, so it is neither - and it
    /// carries Core's own wording for why the MAC is missing.
    /// </summary>
    [Fact]
    public void WillNotGuessWhenThereIsNoMacToTellTwoDevicesApart()
    {
        PlanConformanceReport report = PlanConformance.Compare(
            [Planned(1, Drive, "192.168.1.51")],
            Scan(Unidentified("192.168.1.51")));

        PlanFinding finding = Assert.Single(report.Findings);

        Assert.Equal(PlanFindingKind.AddressUnattributable, finding.Kind);
        Assert.Equal(EventSeverity.Warn, finding.Severity);
        Assert.False(report.HasConflicts);
        Assert.Contains("ARP cache", finding.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A row planned with a MAC and no address yet - the normal shape at 7am - whose device is on
    /// the network. Worth saying, and still not an address the tool fills in: it reports what the
    /// device holds, and the cell stays the user's to fill off the drawing.
    /// </summary>
    [Fact]
    public void SaysWhereAPlannedDeviceIsWhenItsAddressCellIsStillEmpty()
    {
        PlanConformanceReport report = PlanConformance.Compare(
            [new DeviceRecord { Id = 7, Mac = Drive, Role = "spare drive" }],
            Scan(Answering("192.168.1.90", Drive)));

        PlanFinding finding = Assert.Single(report.Findings);

        Assert.Equal(PlanFindingKind.PlannedDeviceUnaddressed, finding.Kind);
        Assert.Equal(EventSeverity.Info, finding.Severity);
        Assert.Equal(0, report.PlannedWithAddress);
        Assert.Contains("not a suggestion", finding.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Inventory. Most segments carry switches, HMIs and last year's work, and none of it belongs
    /// in a commissioning plan - so this is graded as information rather than as a fault.
    /// </summary>
    [Fact]
    public void ListsDevicesThatAreNotInThePlanWithoutCallingThemAFault()
    {
        PlanConformanceReport report = PlanConformance.Compare(
            [Planned(1, Drive, "192.168.1.51")],
            Scan(
                Answering("192.168.1.51", Drive),
                Answering("192.168.1.5", Hmi, "PanelView 800"),
                Answering("192.168.1.6", Stranger, "1783-BMS10CGP")));

        Assert.Equal(2, report.Count(PlanFindingKind.NotInPlan));
        Assert.Equal(1, report.ConfirmedCount);
        Assert.False(report.HasConflicts);
        Assert.Equal(EventSeverity.Info, report.Severity);
    }

    /// <summary>
    /// A device is spoken about once. The thing sitting on somebody's planned address is reported
    /// against that row, not also as an unplanned stranger - two lines about one device is how a
    /// list of six findings becomes a list of eleven nobody finishes reading.
    /// </summary>
    [Fact]
    public void ReportsEachDeviceExactlyOnce()
    {
        PlanConformanceReport occupied = PlanConformance.Compare(
            [Planned(1, Drive, "192.168.1.51")],
            Scan(Answering("192.168.1.51", Hmi, "PanelView 800")));

        Assert.Single(occupied.Findings);

        PlanConformanceReport moved = PlanConformance.Compare(
            [Planned(1, Drive, "192.168.1.51")],
            Scan(Answering("192.168.1.77", Drive)));

        Assert.Equal(PlanFindingKind.PlannedDeviceElsewhere, Assert.Single(moved.Findings).Kind);
    }

    /// <summary>
    /// Worst first, and inside one severity by address as a number rather than as text - otherwise
    /// .100 sorts above .2 and a list read top to bottom stops matching the panel.
    /// </summary>
    [Fact]
    public void OrdersFindingsWorstFirstThenByAddressNumerically()
    {
        PlanConformanceReport report = PlanConformance.Compare(
            [
                Planned(1, Drive, "192.168.1.100"),
                Planned(2, Hmi, "192.168.1.2"),
                Planned(3, Stranger, "192.168.1.9"),
            ],
            Scan(
                Answering("192.168.1.100", MacAddress.Parse("00:11:22:33:44:55")),
                Answering("192.168.1.2", MacAddress.Parse("00:11:22:33:44:66")),
                Answering("192.168.1.9", Stranger)));

        Assert.Collection(
            report.Findings,
            first => Assert.Equal("192.168.1.2", first.Address?.ToString()),
            second => Assert.Equal("192.168.1.100", second.Address?.ToString()),
            third => Assert.Equal(PlanFindingKind.Confirmed, third.Kind));
    }

    /// <summary>
    /// A half-typed row with no MAC matches no device, and must not be allowed to match one by
    /// accident through an empty address comparing equal to another empty one.
    /// </summary>
    [Fact]
    public void APlanRowWithNoMacMatchesNothing()
    {
        PlanConformanceReport report = PlanConformance.Compare(
            [new DeviceRecord { Id = 9, Mac = MacAddress.Empty, PlannedIp = IPAddress.Parse("192.168.1.51") }],
            Scan(Unidentified("192.168.1.51")));

        Assert.Equal(PlanFindingKind.AddressUnattributable, Assert.Single(report.Findings).Kind);
    }

    /// <summary>
    /// The summary is what the event log stores and what the status line shows, so it leads with
    /// the count that was compared. "8 devices answered" is only reassuring until somebody notices
    /// the plan has fourteen rows in it.
    /// </summary>
    [Fact]
    public void SummarisesAgainstThePlanRatherThanAgainstWhatAnswered()
    {
        PlanConformanceReport report = PlanConformance.Compare(
            [
                Planned(1, Drive, "192.168.1.51", "conveyor drive"),
                Planned(2, Hmi, "192.168.1.20"),
            ],
            Scan(
                Answering("192.168.1.51", Stranger, "1783-BMS10CGP"),
                Answering("192.168.1.20", Hmi)));

        Assert.Contains("1 of the 2 planned addresses answered as planned", report.Summary, StringComparison.Ordinal);
        Assert.Contains("1 planned address is held by a different device", report.Summary, StringComparison.Ordinal);
        Assert.Equal(EventSeverity.Error, report.Severity);
    }

    /// <summary>A plan of MACs with no addresses yet cannot be contradicted by anything.</summary>
    [Fact]
    public void SaysSoWhenThereIsNothingToCompareAgainst()
    {
        PlanConformanceReport report = PlanConformance.Compare(
            [],
            Scan(Answering("192.168.1.5", Hmi, "PanelView 800")));

        Assert.Contains("no plan row has an address", report.Summary, StringComparison.Ordinal);
        Assert.Equal(0, report.PlannedWithAddress);
    }

    private static DeviceRecord Planned(long id, MacAddress mac, string ip, string? role = null) => new()
    {
        Id = id,
        Mac = mac,
        PlannedIp = IPAddress.Parse(ip),
        PlannedMask = IPAddress.Parse("255.255.255.0"),
        Role = role,
    };

    private static DiscoveredDevice Answering(
        string address, MacAddress mac, string product = "1734-AENT/B", bool contested = false) => new()
    {
        Identity = Identities.Reply(address: address, product: product, serial: (uint)mac.GetHashCode()),
        Mac = mac,
        MacSource = MacSource.Arp,
        Vendor = "Rockwell Automation",
        AddressIsContested = contested,
    };

    /// <summary>A device that answered and is not in this machine's ARP cache, so has no MAC.</summary>
    private static DiscoveredDevice Unidentified(string address) => new()
    {
        Identity = Identities.Reply(address: address),
        MacSource = MacSource.NotInArp,
    };

    private static DiscoveryResult Scan(params DiscoveredDevice[] devices) =>
        new(
            new ScanReport(
                Nics.Healthy(),
                [.. devices.Select(d => d.Identity)],
                ProbesSent: 1,
                Duplicates: 0,
                Ignored: 0,
                Refused: 0,
                Duration: TimeSpan.FromSeconds(2)),
            devices);
}
