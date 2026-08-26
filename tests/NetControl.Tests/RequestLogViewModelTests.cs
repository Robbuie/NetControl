using NetControl.App.ViewModels;
using NetControl.Core;
using NetControl.Core.Dhcp;
using NetControl.Core.Interfaces;
using NetControl.Core.Oui;
using Xunit;

namespace NetControl.Tests;

/// <summary>
/// The log's value is that it stays readable while a panel full of devices retransmits at it, and
/// that it never quietly turns a stranger on the network into a device you were expecting.
/// </summary>
public sealed class RequestLogViewModelTests
{
    private static readonly MacAddress Planned = MacAddress.Parse("00:1D:9C:C7:B0:70");
    private static readonly MacAddress Stranger = MacAddress.Parse("00:0F:73:11:22:33");

    [Fact]
    public void RecordsARequestThatIsNotInThePlanAsAStranger()
    {
        RequestLogViewModel log = Log();

        log.OnRequest(Events.Request(Stranger, AssignmentDecision.Ignore($"{Stranger} is not in the plan")));

        LogEntryViewModel entry = Assert.Single(log.Entries);
        Assert.Equal(RequestOutcome.NotInPlan, entry.Outcome);
        Assert.True(entry.IsUnknownDevice);
        Assert.Equal(1, log.UnknownDeviceCount);
    }

    /// <summary>
    /// The distinction that earns <see cref="NetControl.App.Serving.IPlanIndex"/> its keep. A planned device the policy
    /// declined is a device you are waiting for, not a stranger, and colouring the two the same
    /// would bury the row that matters.
    /// </summary>
    [Fact]
    public void APlannedDeviceThePolicyDeclinedIsNotAStranger()
    {
        RequestLogViewModel log = Log();

        log.OnRequest(Events.Request(
            Planned,
            AssignmentDecision.Ignore("planned address 10.0.0.5 is not on the subnet of [12] Ethernet")));

        LogEntryViewModel entry = Assert.Single(log.Entries);
        Assert.Equal(RequestOutcome.Refused, entry.Outcome);
        Assert.False(entry.IsUnknownDevice);
        Assert.Equal(0, log.UnknownDeviceCount);
        Assert.Contains("not on the subnet", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WatchModeNeverClaimsAnAddressWasHandedOut()
    {
        RequestLogViewModel log = Log();
        log.Mode = DhcpServerMode.Watch;

        log.OnRequest(Events.Request(
            Planned,
            AssignmentDecision.Serve(Events.Assignment(Planned), $"{Planned} is planned as 192.168.1.51")));

        Assert.Equal(RequestOutcome.Watching, Assert.Single(log.Entries).Outcome);
        Assert.Equal(0, log.ServedCount);
    }

    [Fact]
    public void ServeModeMarksAPlannedDeviceAsServed()
    {
        RequestLogViewModel log = Log();
        log.Mode = DhcpServerMode.Serve;

        log.OnRequest(Events.Request(
            Planned,
            AssignmentDecision.Serve(Events.Assignment(Planned), $"{Planned} is planned as 192.168.1.51")));

        Assert.Equal(RequestOutcome.Served, Assert.Single(log.Entries).Outcome);
    }

    [Fact]
    public void MarksARequestThatLandedOnAnotherAdapter()
    {
        RequestLogViewModel log = Log();
        log.SelectedInterfaceIndex = 12;

        NicInfo elsewhere = Nics.Healthy(index: 4) with { Name = "vEthernet (Default Switch)" };

        log.OnRequest(Events.Request(
            Planned,
            AssignmentDecision.Ignore("arrived on [4] vEthernet (Default Switch), which is not the selected adapter"),
            nic: elsewhere));

        LogEntryViewModel entry = Assert.Single(log.Entries);
        Assert.Equal(RequestOutcome.WrongAdapter, entry.Outcome);
        Assert.Contains("[4] vEthernet (Default Switch)", entry.AdapterText, StringComparison.Ordinal);
    }

    /// <summary>
    /// A device that has not been answered asks several times a second. A hundred identical rows
    /// tell the reader nothing the first one did not, and they bury the row that explains why.
    /// </summary>
    [Fact]
    public void CollapsesARetransmitIntoACounterRatherThanANewRow()
    {
        RequestLogViewModel log = Log();
        AssignmentDecision ignore = AssignmentDecision.Ignore($"{Stranger} is not in the plan");

        log.OnRequest(Events.Request(Stranger, ignore));
        log.OnRequest(Events.Request(Stranger, ignore, isRetransmit: true));
        log.OnRequest(Events.Request(Stranger, ignore, isRetransmit: true));

        LogEntryViewModel entry = Assert.Single(log.Entries);
        Assert.Equal(3, entry.RepeatCount);
        Assert.True(entry.IsRepeated);
        Assert.Equal("x3", entry.RepeatText);

        // The counter is presentation. Every request still counted as a request.
        Assert.Equal(1, log.RequestCount);
    }

    [Fact]
    public void ANewTransactionFromTheSameDeviceIsANewRow()
    {
        RequestLogViewModel log = Log();
        AssignmentDecision ignore = AssignmentDecision.Ignore($"{Stranger} is not in the plan");

        log.OnRequest(Events.Request(Stranger, ignore, transactionId: 0x1111_1111));
        log.OnRequest(Events.Request(Stranger, ignore, transactionId: 0x2222_2222, isRetransmit: true));

        Assert.Equal(2, log.Entries.Count);
    }

    [Fact]
    public void TwoDevicesInterleavingDoNotFoldIntoEachOther()
    {
        RequestLogViewModel log = Log();

        log.OnRequest(Events.Request(Planned, AssignmentDecision.Ignore("a"), transactionId: 1));
        log.OnRequest(Events.Request(Stranger, AssignmentDecision.Ignore("b"), transactionId: 2));
        log.OnRequest(Events.Request(Planned, AssignmentDecision.Ignore("a"), transactionId: 1, isRetransmit: true));

        Assert.Equal(2, log.Entries.Count);
        Assert.Equal(2, log.Entries.Single(e => e.Mac == Planned).RepeatCount);
        Assert.Equal(1, log.Entries.Single(e => e.Mac == Stranger).RepeatCount);
    }

    [Fact]
    public void NewestRowIsFirst()
    {
        RequestLogViewModel log = Log();

        log.OnRequest(Events.Request(Planned, AssignmentDecision.Ignore("first"), transactionId: 1));
        log.OnRequest(Events.Request(Stranger, AssignmentDecision.Ignore("second"), transactionId: 2));

        Assert.Equal("second", log.Entries[0].Message);
    }

    [Fact]
    public void ARepliedAddressIsCountedOnce()
    {
        RequestLogViewModel log = Log();

        log.OnReply(Events.Reply(Events.Assignment(Planned)));

        LogEntryViewModel entry = Assert.Single(log.Entries);
        Assert.Equal(LogEntryKind.Reply, entry.Kind);
        Assert.Equal(1, log.ServedCount);
        Assert.Contains("192.168.1.51", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFatalFaultCarriesItsRemediation()
    {
        RequestLogViewModel log = Log();

        log.OnFault(new DhcpFaultEventArgs(
            Events.At,
            "The listening socket failed: NetworkDown. The server has stopped.",
            "Restart the server. If it recurs, check whether the adapter was removed.",
            isFatal: true));

        LogEntryViewModel entry = Assert.Single(log.Entries);
        Assert.Equal(LogEntryKind.Fault, entry.Kind);
        Assert.Equal(1, log.FaultCount);
        Assert.Contains("Restart the server", entry.Tooltip, StringComparison.Ordinal);
    }

    /// <summary>
    /// Core reports an advisory port conflict as a fault, and it fires on every start. The bar
    /// grades that same finding amber, so a red row saying the identical sentence would leave the
    /// user deciding which of the two to believe.
    /// </summary>
    [Fact]
    public void ANonFatalFaultIsAWarningRatherThanAFault()
    {
        RequestLogViewModel log = Log();

        log.OnFault(new DhcpFaultEventArgs(
            Events.At,
            "UDP/67 is bound by svchost (pid 5048) on 172.22.160.1, but to specific addresses "
                + "rather than 0.0.0.0, so it only competes for requests arriving on those subnets."));

        Assert.Equal(LogEntryKind.Warning, Assert.Single(log.Entries).Kind);
    }

    /// <summary>
    /// Rows do not arrive in timestamp order: Core stamps an event on the receive loop and it
    /// reaches the UI thread through a queued post, while a notice the app raises about itself is
    /// added inline. That really happened - a "Watching" line stamped 40.747 sat below a fault
    /// stamped 40.743 - and a log whose timestamps do not descend is a log people stop trusting.
    /// </summary>
    [Fact]
    public void AnEarlierRowThatArrivesLateSortsBelowTheNewerOne()
    {
        var clock = new TestTimeProvider(Events.At);
        var log = new RequestLogViewModel(OuiDatabase.Empty, new FakePlanIndex(Planned), clock);

        clock.Advance(TimeSpan.FromMilliseconds(4));
        log.AddNotice("Watching. Nothing will be transmitted.");

        // Stamped before the notice, but handed over after it.
        log.OnFault(new DhcpFaultEventArgs(Events.At, "UDP/67 is bound by svchost (pid 5048)."));

        Assert.Equal(2, log.Entries.Count);
        Assert.StartsWith("Watching", log.Entries[0].Message, StringComparison.Ordinal);
        Assert.StartsWith("UDP/67", log.Entries[1].Message, StringComparison.Ordinal);
        Assert.True(log.Entries[0].Timestamp >= log.Entries[1].Timestamp);
    }

    [Fact]
    public void KeepsTheFilteredViewInStepWithAnOutOfOrderInsert()
    {
        RequestLogViewModel log = Log();

        // A stranger, then an older row that the filter will hide, then a newer stranger.
        log.OnRequest(Events.Request(Stranger, AssignmentDecision.Ignore("first"), transactionId: 1));
        log.OnFault(new DhcpFaultEventArgs(Events.At.AddMilliseconds(-5), "an older warning"));
        log.OnRequest(Events.Request(
            Stranger,
            AssignmentDecision.Ignore("second"),
            transactionId: 2,
            at: Events.At.AddMilliseconds(5)));

        log.ShowOnlyUnknownDevices = true;

        Assert.Equal(2, log.Entries.Count);
        Assert.Equal("second", log.Entries[0].Message);
        Assert.Equal("first", log.Entries[1].Message);
    }

    [Fact]
    public void TheUnknownOnlyFilterHidesEverythingElse()
    {
        RequestLogViewModel log = Log();

        log.OnRequest(Events.Request(Planned, AssignmentDecision.Ignore("planned but refused"), transactionId: 1));
        log.OnRequest(Events.Request(Stranger, AssignmentDecision.Ignore("not in the plan"), transactionId: 2));

        Assert.Equal(2, log.Entries.Count);

        log.ShowOnlyUnknownDevices = true;

        Assert.Equal(Stranger, Assert.Single(log.Entries).Mac);

        log.ShowOnlyUnknownDevices = false;

        Assert.Equal(2, log.Entries.Count);
    }

    [Fact]
    public void DropsTheOldestRowsRatherThanGrowingForever()
    {
        var log = new RequestLogViewModel(OuiDatabase.Empty, new FakePlanIndex(), new TestTimeProvider(Events.At))
        {
            Capacity = 3,
        };

        for (uint i = 0; i < 5; i++)
        {
            log.OnRequest(Events.Request(Stranger, AssignmentDecision.Ignore($"request {i}"), transactionId: i));
        }

        Assert.Equal(3, log.Entries.Count);
        Assert.Equal("request 4", log.Entries[0].Message);
        Assert.Equal("request 2", log.Entries[2].Message);
    }

    /// <summary>
    /// The vendor is resolved once, when the row is made, from the packed IEEE table. An address
    /// the table does not know reads as unknown rather than as a plausible guess.
    /// </summary>
    [Fact]
    public void AnUnresolvedVendorReadsAsUnknownRatherThanBlank()
    {
        RequestLogViewModel log = Log();

        log.OnRequest(Events.Request(Stranger, AssignmentDecision.Ignore("not in the plan")));

        Assert.Equal("unknown vendor", Assert.Single(log.Entries).VendorText);
    }

    /// <summary>
    /// The reason the log knows about the plan at all beyond a yes/no. A bench session is read
    /// against a printed plan, and a row that says "Conveyor 3 drive" finds the line on that sheet
    /// a great deal faster than twelve hex digits do.
    /// </summary>
    [Fact]
    public void APlannedDeviceIsLoggedUnderTheNameThePlanGaveIt()
    {
        var plan = new FakePlanIndex().Named(Planned, "Conveyor 3 drive");
        var log = new RequestLogViewModel(OuiDatabase.Empty, plan, new TestTimeProvider(Events.At));

        log.OnRequest(Events.Request(Planned, AssignmentDecision.Ignore("planned but refused")));

        Assert.Equal("Conveyor 3 drive", Assert.Single(log.Entries).NameText);
    }

    /// <summary>
    /// A stranger's row is blank rather than carrying a word for the absence of a name. It is
    /// already coloured as a stranger; "unknown" in a name column reads like a name.
    /// </summary>
    [Fact]
    public void AStrangerCarriesNoName()
    {
        RequestLogViewModel log = Log();

        log.OnRequest(Events.Request(Stranger, AssignmentDecision.Ignore("not in the plan")));

        LogEntryViewModel entry = Assert.Single(log.Entries);
        Assert.Equal(string.Empty, entry.NameText);
        Assert.True(entry.IsUnknownDevice);
    }

    /// <summary>
    /// The regression the old policy-backed index caused. "Add to plan" from a log row stores a MAC
    /// with no address - deliberately, because the tool never invents one - and that row cannot
    /// become an assignment, so it was absent from the serving policy. Reading the plan off the
    /// policy therefore filed the device as a stranger for as long as it took to type an address,
    /// which is precisely the minute the user is watching the log.
    /// </summary>
    [Fact]
    public void ADeviceInThePlanWithNoAddressYetIsNotAStranger()
    {
        // FakePlanIndex's constructor plans a MAC with no name and no address, which is what that
        // gesture leaves behind.
        var log = new RequestLogViewModel(
            OuiDatabase.Empty,
            new FakePlanIndex(Planned),
            new TestTimeProvider(Events.At));

        log.OnRequest(Events.Request(Planned, AssignmentDecision.Ignore($"{Planned} has no planned address")));

        LogEntryViewModel entry = Assert.Single(log.Entries);
        Assert.Equal(RequestOutcome.Refused, entry.Outcome);
        Assert.False(entry.IsUnknownDevice);
        Assert.Equal(0, log.UnknownDeviceCount);
        Assert.Equal(string.Empty, entry.NameText);
    }

    /// <summary>
    /// A log row is an account of a moment. Renaming a device afterwards must not reach back and
    /// retitle the requests it made before it had that name - a commissioning record that rewrites
    /// itself to match the current plan is not a record.
    /// </summary>
    [Fact]
    public void RenamingADeviceDoesNotRetitleRowsAlreadyLogged()
    {
        var plan = new FakePlanIndex().Named(Planned, "Drive A");
        var log = new RequestLogViewModel(OuiDatabase.Empty, plan, new TestTimeProvider(Events.At));

        log.OnRequest(Events.Request(Planned, AssignmentDecision.Ignore("planned but refused"), transactionId: 1));

        plan.Named(Planned, "Drive B");
        log.OnRequest(Events.Request(Planned, AssignmentDecision.Ignore("planned but refused"), transactionId: 2));

        Assert.Equal("Drive B", log.Entries[0].NameText);
        Assert.Equal("Drive A", log.Entries[1].NameText);
    }

    /// <summary>The reply row is the one the commissioning record turns on, so it is named too.</summary>
    [Fact]
    public void AReplyIsLoggedUnderTheDeviceName()
    {
        var plan = new FakePlanIndex().Named(Planned, "Conveyor 3 drive");
        var log = new RequestLogViewModel(OuiDatabase.Empty, plan, new TestTimeProvider(Events.At));

        log.OnReply(Events.Reply(Events.Assignment(Planned)));

        Assert.Equal("Conveyor 3 drive", Assert.Single(log.Entries).NameText);
    }

    private static RequestLogViewModel Log() =>
        new(OuiDatabase.Empty, new FakePlanIndex(Planned), new TestTimeProvider(Events.At));
}
