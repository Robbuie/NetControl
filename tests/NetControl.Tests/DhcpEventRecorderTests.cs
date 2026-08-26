using System.Net;
using NetControl.Core;
using NetControl.Core.Dhcp;
using NetControl.Core.Interfaces;
using NetControl.Core.Persistence;
using Xunit;

namespace NetControl.Tests;

/// <summary>
/// The wiring that turns the engine's events into a commissioning record.
///
/// These drive the recorder with hand-built events rather than a real socket, because the reply
/// path cannot be reached hermetically any other way: making the server actually send a reply
/// means broadcasting onto whatever network the laptop is plugged into, and a `dotnet test` run
/// must never do that. The one test here that uses a real socket stays in Watch mode, which
/// transmits nothing.
/// </summary>
public class DhcpEventRecorderTests
{
    private static readonly MacAddress Mac = MacAddress.Parse(Frames.DeviceMac);

    private static readonly DateTimeOffset When = new(2026, 8, 6, 9, 15, 0, TimeSpan.Zero);

    private static DeviceAssignment Planned => new(
        Mac,
        IPAddress.Parse("192.168.1.51"),
        IPAddress.Parse("255.255.255.0"),
        IPAddress.Parse("192.168.1.1"));

    [Fact]
    public void RecordsARequestAgainstThePlannedDeviceItCameFrom()
    {
        using ProjectStore store = ProjectStore.CreateInMemory();
        long deviceId = store.Devices.Upsert(PlannedRow());

        using DhcpEventRecorder recorder = Recorder(store);
        recorder.RecordRequest(RequestArgs(AssignmentDecision.Serve(Planned, $"{Mac} is planned as 192.168.1.51")));

        EventRecord row = Assert.Single(store.Events.All());

        Assert.Equal(EventSeverity.Info, row.Severity);
        Assert.Equal(EventCategory.Dhcp, row.Category);
        Assert.Equal(deviceId, row.DeviceId);
        Assert.Equal(Mac.ToString(), row.Target);
        Assert.Contains("[12] Ethernet", row.Message, StringComparison.Ordinal);
        Assert.Contains("planned as 192.168.1.51", row.Message, StringComparison.Ordinal);
        Assert.Contains("\"xid\":\"FC5D1465\"", row.Detail!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same path, but driven by a real datagram on a real socket, which is the only way to
    /// prove the subscription is wired up at all - every other test here calls the recorder
    /// directly and would still pass if the constructor never subscribed to anything.
    ///
    /// Watch mode, so nothing is transmitted.
    /// </summary>
    [Fact]
    public async Task ARequestOffTheWireLandsInTheProjectFile()
    {
        using ProjectStore store = ProjectStore.CreateInMemory();
        await using WireHarness harness = await WireHarness.StartAsync(new StaticMapPolicy([Planned]));

        long deviceId = store.Devices.Upsert(PlannedRow());
        using DhcpEventRecorder recorder = new(store, harness.Server);

        // Subscribed after the recorder, so by the time this fires the recorder's handler has
        // already run: the server invokes handlers in subscription order, on one thread. Waiting
        // on the harness's own channel would race, because it subscribed first.
        var recorded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Server.RequestReceived += (_, _) => recorded.TrySetResult();

        harness.Send(Frames.Bytes(Frames.BootpRequest));
        await recorded.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, recorder.RecordedCount);
        Assert.Equal(0, recorder.FailureCount);

        EventRecord row = Assert.Single(store.Events.All());
        Assert.Equal(deviceId, row.DeviceId);
        Assert.Equal(Mac.ToString(), row.Target);
    }

    /// <summary>
    /// An unknown MAC asking for an address is ordinary traffic on a plant network, so it is
    /// information. A device that IS in the plan and still did not get served is a
    /// misconfiguration, and that is the line worth being able to find later.
    /// </summary>
    [Fact]
    public void WarnsOnlyWhenAPlannedDeviceWasRefused()
    {
        using ProjectStore store = ProjectStore.CreateInMemory();
        store.Devices.Upsert(PlannedRow());

        using DhcpEventRecorder recorder = Recorder(store);
        recorder.RecordRequest(RequestArgs(AssignmentDecision.Ignore(
            "planned address 192.168.1.51 is not on the subnet of [12] Ethernet")));

        Assert.Equal(EventSeverity.Warn, Assert.Single(store.Events.All()).Severity);
    }

    [Fact]
    public void LeavesAnUnknownMacAsInformationWithNoDeviceAttached()
    {
        using ProjectStore store = ProjectStore.CreateInMemory();

        using DhcpEventRecorder recorder = Recorder(store);
        recorder.RecordRequest(RequestArgs(AssignmentDecision.Ignore($"{Mac} is not in the plan")));

        EventRecord row = Assert.Single(store.Events.All());

        Assert.Equal(EventSeverity.Info, row.Severity);
        Assert.Null(row.DeviceId);
        Assert.Equal(Mac.ToString(), row.Target);
    }

    /// <summary>
    /// "Your device is asking, but on [12] vEthernet (Default Switch)" is the most useful sentence
    /// this tool can produce. It has to survive into the record, not just the live view.
    /// </summary>
    [Fact]
    public void KeepsTheArrivalAdapterInTheRecord()
    {
        using ProjectStore store = ProjectStore.CreateInMemory();

        using DhcpEventRecorder recorder = Recorder(store);
        recorder.RecordRequest(RequestArgsOn(
            Nics.Healthy(index: 27) with { Name = "vEthernet (Default Switch)" },
            AssignmentDecision.Ignore("not the selected adapter")));

        EventRecord row = Assert.Single(store.Events.All());

        Assert.Contains("[27] vEthernet (Default Switch)", row.Message, StringComparison.Ordinal);
        Assert.Contains("\"interfaceIndex\":27", row.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void SaysSoWhenTheArrivalAdapterHasSinceDisappeared()
    {
        using ProjectStore store = ProjectStore.CreateInMemory();

        using DhcpEventRecorder recorder = Recorder(store);
        recorder.RecordRequest(RequestArgsOn(nic: null, AssignmentDecision.Ignore("adapter gone")));

        Assert.Contains(
            "adapter no longer present",
            Assert.Single(store.Events.All()).Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A device that has not been answered asks several times a second. A hundred identical rows
    /// tell the reader nothing the first one did not, and they bury the row that matters.
    /// </summary>
    [Fact]
    public void IgnoresRetransmitsUnlessAskedToKeepThem()
    {
        using ProjectStore store = ProjectStore.CreateInMemory();
        using DhcpEventRecorder quiet = Recorder(store);

        Assert.False(quiet.RecordRequest(RequestArgs(AssignmentDecision.Ignore("repeat"), retransmit: true)));
        Assert.Equal(0, store.Events.Count);

        using var verbose = new DhcpEventRecorder(store, Server()) { RecordRetransmits = true };

        Assert.True(verbose.RecordRequest(RequestArgs(AssignmentDecision.Ignore("repeat"), retransmit: true)));
        Assert.Equal(1, store.Events.Count);
    }

    /// <summary>
    /// Every reply is a state-changing operation, so it produces both a log row and the
    /// Assignment summary of what was actually handed out.
    /// </summary>
    [Fact]
    public void WritesBothALogRowAndAnAssignmentForEveryReply()
    {
        using ProjectStore store = ProjectStore.CreateInMemory();
        long deviceId = store.Devices.Upsert(PlannedRow());

        using DhcpEventRecorder recorder = Recorder(store);
        recorder.RecordReply(ReplyArgs());

        EventRecord row = Assert.Single(store.Events.All());
        Assert.Equal(deviceId, row.DeviceId);
        Assert.Contains("192.168.1.51", row.Message, StringComparison.Ordinal);
        Assert.Contains("\"subnetMask\":\"255.255.255.0\"", row.Detail!, StringComparison.Ordinal);

        AssignmentRecord served = Assert.Single(store.Assignments.ForDevice(deviceId));
        Assert.Equal(IPAddress.Parse("192.168.1.51"), served.ServedIp);
        Assert.Equal(12, served.NicIndex);
        Assert.Equal("Ethernet", served.NicName);
        Assert.Equal(When, served.ServedUtc);
    }

    [Fact]
    public void RecordsAFatalFaultAsAnErrorAndKeepsTheRemediation()
    {
        using ProjectStore store = ProjectStore.CreateInMemory();

        using DhcpEventRecorder recorder = Recorder(store);
        recorder.RecordFault(new DhcpFaultEventArgs(
            When,
            "UDP/67 is held by vmnetdhcp.exe (pid 4312).",
            "Stop the VMware DHCP service and retry.",
            isFatal: true));

        EventRecord row = Assert.Single(store.Events.All());

        Assert.Equal(EventSeverity.Error, row.Severity);
        Assert.Contains("vmnetdhcp.exe", row.Message, StringComparison.Ordinal);
        Assert.Contains("Stop the VMware DHCP service", row.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordsANonFatalFaultAsAWarning()
    {
        using ProjectStore store = ProjectStore.CreateInMemory();

        using DhcpEventRecorder recorder = Recorder(store);
        recorder.RecordFault(new DhcpFaultEventArgs(When, "A malformed datagram was ignored."));

        Assert.Equal(EventSeverity.Warn, Assert.Single(store.Events.All()).Severity);
    }

    [Fact]
    public void StopsRecordingOnceDisposed()
    {
        using ProjectStore store = ProjectStore.CreateInMemory();
        DhcpServer server = Server();

        var recorder = new DhcpEventRecorder(store, server);
        recorder.Dispose();

        // Disposing twice must be harmless - the UI closes a project without knowing whether the
        // server was ever started.
        recorder.Dispose();

        Assert.Equal(0, store.Events.Count);
    }

    /// <summary>
    /// The rule the recorder exists under: nothing that happens while writing a log row may stop
    /// the tool answering a device. This runs a real server on a real socket, in Watch mode so
    /// nothing is transmitted, against a project that has already been closed underneath it.
    /// </summary>
    [Fact]
    public async Task AFailedWriteIsReportedRatherThanKillingTheReceiveLoop()
    {
        ProjectStore store = ProjectStore.CreateInMemory();
        await using WireHarness harness = await WireHarness.StartAsync(new StaticMapPolicy([Planned]));

        using DhcpEventRecorder recorder = new(store, harness.Server);
        var failed = new TaskCompletionSource<PersistenceFailedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        recorder.RecordingFailed += (_, e) => failed.TrySetResult(e);

        store.Dispose();
        harness.Send(Frames.Bytes(Frames.BootpRequest));

        PersistenceFailedEventArgs failure = await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("request", failure.What);
        Assert.IsType<ObjectDisposedException>(failure.Exception);
        Assert.Equal(1, recorder.FailureCount);

        // The engine carried on: it still saw and reported the request.
        Assert.Equal(Mac, (await harness.NextRequestAsync()).Mac);
        Assert.Empty(harness.Faults());
    }

    private static DhcpEventRecorder Recorder(ProjectStore store) => new(store, Server());

    private static DhcpServer Server() =>
        new(new FakeNicInventory(), new StaticMapPolicy([Planned]));

    private static DeviceRecord PlannedRow() => new()
    {
        Mac = Mac,
        PlannedIp = IPAddress.Parse("192.168.1.51"),
        PlannedMask = IPAddress.Parse("255.255.255.0"),
        PlannedGateway = IPAddress.Parse("192.168.1.1"),
    };

    private static DhcpRequestEventArgs RequestArgs(AssignmentDecision decision, bool retransmit = false) =>
        RequestArgsOn(Nics.Healthy(), decision, retransmit);

    /// <param name="nic">Null models the adapter having gone away between arrival and lookup.</param>
    private static DhcpRequestEventArgs RequestArgsOn(
        NicInfo? nic, AssignmentDecision decision, bool retransmit = false) =>
        new(
            When,
            new BootpPacket
            {
                Op = BootpPacket.OpBootRequest,
                ClientMac = Mac,
                TransactionId = 0xFC5D1465,
                Flags = 0x8000,
            },
            nic?.Index ?? 12,
            nic,
            IPAddress.Broadcast,
            retransmit,
            decision);

    private static DhcpReplyEventArgs ReplyArgs()
    {
        var request = new BootpPacket
        {
            Op = BootpPacket.OpBootRequest,
            ClientMac = Mac,
            TransactionId = 0xFC5D1465,
        };

        var reply = new BootpPacket
        {
            Op = BootpPacket.OpBootReply,
            ClientMac = Mac,
            TransactionId = 0xFC5D1465,
            YourIp = IPAddress.Parse("192.168.1.51"),
        };

        return new DhcpReplyEventArgs(
            When,
            request,
            reply,
            Planned,
            Nics.Healthy(),
            ReplySendMode.PerSocketBind,
            new IPEndPoint(IPAddress.Broadcast, 68),
            300);
    }
}
