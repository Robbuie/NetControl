using System.Net;
using System.Text;
using NetControl.Core.Interfaces;
using NetControl.Core.Persistence;
using NetControl.Core.Tftp;
using Xunit;

namespace NetControl.Tests;

/// <summary>
/// The wiring that turns the watch's events into a commissioning record.
///
/// <para>Mostly hand-built events rather than a real socket, exactly as
/// <see cref="DhcpEventRecorderTests"/> does. The one test that uses a socket is there to prove the
/// subscription exists at all - every other test here calls the recorder directly and would still
/// pass if the constructor never subscribed to anything.</para>
/// </summary>
public class TftpEventRecorderTests
{
    private static readonly DateTimeOffset When = new(2026, 9, 10, 9, 15, 0, TimeSpan.Zero);

    [Fact]
    public void RecordsARequestWithTheFilenameAndTheOptionsItCarried()
    {
        using ProjectStore store = ProjectStore.CreateInMemory();
        using TftpEventRecorder recorder = Recorder(store);

        recorder.RecordRequest(RequestArgs(options: new TftpOptions(
        [
            new TftpOption("blksize", "1468"),
            new TftpOption("tsize", "67108864"),
        ])));

        EventRecord row = Assert.Single(store.Events.All());

        Assert.Equal(EventCategory.Tftp, row.Category);
        Assert.Equal(EventSeverity.Info, row.Severity);
        Assert.Contains("FROM00.IMG", row.Message, StringComparison.Ordinal);
        Assert.Contains("\"fileName\":\"FROM00.IMG\"", row.Detail!, StringComparison.Ordinal);
        Assert.Contains("blksize=1468", row.Detail!, StringComparison.Ordinal);
        Assert.Equal(0, recorder.FailureCount);
    }

    [Fact]
    public void CarriesTheSourceAddressAsTheTargetAndNeverGuessesAtADevice()
    {
        // TFTP has no hardware address in it. Turning a source IP into a planned device would be
        // a join this tool has no evidence for, and the rest of the product does not invent those.
        using ProjectStore store = ProjectStore.CreateInMemory();
        using TftpEventRecorder recorder = Recorder(store);

        recorder.RecordRequest(RequestArgs());

        EventRecord row = Assert.Single(store.Events.All());

        Assert.Equal("192.168.1.51", row.Target);
        Assert.Null(row.DeviceId);
    }

    [Fact]
    public void RecordsAnOrdinaryRefusedRequestAsInformationRatherThanAWarning()
    {
        // Severity is about whether something needs fixing. In Observe mode every request is
        // refused by design, so refusing is not itself a finding.
        using ProjectStore store = ProjectStore.CreateInMemory();
        using TftpEventRecorder recorder = Recorder(store);

        recorder.RecordRequest(RequestArgs());

        Assert.Equal(EventSeverity.Info, Assert.Single(store.Events.All()).Severity);
    }

    [Fact]
    public void WarnsAboutARequestThatWouldCorruptTheImage()
    {
        using ProjectStore store = ProjectStore.CreateInMemory();
        using TftpEventRecorder recorder = Recorder(store);

        recorder.RecordRequest(RequestArgs(mode: TftpTransferMode.NetAscii, rawMode: "netascii"));

        EventRecord row = Assert.Single(store.Events.All());

        Assert.Equal(EventSeverity.Warn, row.Severity);
        Assert.Contains("concern", row.Detail!, StringComparison.Ordinal);
        Assert.Contains("corrupted", row.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void WarnsWhenAControllerDeclaresASizeItsBlockSizeCannotCarry()
    {
        // The size came from the controller, not from a guess, and 128 MiB does not fit in 65535
        // blocks of 1468 - the correction the Python port produced, now reaching the event log.
        using ProjectStore store = ProjectStore.CreateInMemory();
        using TftpEventRecorder recorder = Recorder(store);

        recorder.RecordRequest(RequestArgs(options: new TftpOptions(
        [
            new TftpOption("blksize", "1468"),
            new TftpOption("tsize", "134217728"),
        ])));

        EventRecord row = Assert.Single(store.Events.All());

        Assert.Equal(EventSeverity.Warn, row.Severity);
        Assert.Contains("block counter to wrap", row.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void DoesNotWarnAboutAPlainRequestThatSimplyOffersNoOptions()
    {
        // The rule this replaced fired on every RFC 1350 request, which made "ordinary" impossible
        // and would have made the warning column meaningless.
        using ProjectStore store = ProjectStore.CreateInMemory();
        using TftpEventRecorder recorder = Recorder(store);

        recorder.RecordRequest(RequestArgs());

        Assert.Equal(EventSeverity.Info, Assert.Single(store.Events.All()).Severity);
    }

    [Fact]
    public void WarnsWhenTheRefusalCouldNotBeSent()
    {
        using ProjectStore store = ProjectStore.CreateInMemory();
        using TftpEventRecorder recorder = Recorder(store);

        recorder.RecordRequest(RequestArgs(action: TftpWatchAction.SendFailed, reason: "the refusal failed"));

        Assert.Equal(EventSeverity.Warn, Assert.Single(store.Events.All()).Severity);
    }

    [Fact]
    public void KeepsTheRawBytesOfAFilenameThatWillNotPrint()
    {
        // The decoded string is not evidence when the name is not printable; the bytes are, and
        // hex is what a capture shows.
        using ProjectStore store = ProjectStore.CreateInMemory();
        using TftpEventRecorder recorder = Recorder(store);

        recorder.RecordRequest(RequestArgs(fileName: "FROM\u0001.IMG"));

        string detail = Assert.Single(store.Events.All()).Detail!;

        Assert.Contains("fileNameBytes", detail, StringComparison.Ordinal);
        Assert.Contains("46524F4D012E494D47", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void LeavesTheHexOutWhenTheFilenameReadsPerfectlyWell()
    {
        using ProjectStore store = ProjectStore.CreateInMemory();
        using TftpEventRecorder recorder = Recorder(store);

        recorder.RecordRequest(RequestArgs());

        Assert.DoesNotContain("fileNameBytes", Assert.Single(store.Events.All()).Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void SkipsARetransmitUnlessItWasAskedToKeepThem()
    {
        using ProjectStore store = ProjectStore.CreateInMemory();
        using TftpEventRecorder recorder = Recorder(store);

        Assert.False(recorder.RecordRequest(RequestArgs(isRetransmit: true)));
        Assert.Empty(store.Events.All());
    }

    [Fact]
    public void KeepsARetransmitWhenTheQuestionIsAboutHowAControllerRetries()
    {
        using ProjectStore store = ProjectStore.CreateInMemory();
        using TftpEventRecorder recorder = new(store, Server()) { RecordRetransmits = true };

        Assert.True(recorder.RecordRequest(RequestArgs(isRetransmit: true)));
        Assert.Contains("\"retransmit\":true", Assert.Single(store.Events.All()).Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordsTheWatchStartingSoTheRecordSaysWhatWasArmed()
    {
        using ProjectStore store = ProjectStore.CreateInMemory();
        using TftpEventRecorder recorder = Recorder(store);

        recorder.RecordListening();

        EventRecord row = Assert.Single(store.Events.All());

        Assert.Equal(EventCategory.Tftp, row.Category);
        Assert.Contains("Observe", row.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, EventSeverity.Warn)]
    [InlineData(true, EventSeverity.Error)]
    public void GradesAFaultByWhetherTheWatchIsStillRunning(bool fatal, EventSeverity expected)
    {
        using ProjectStore store = ProjectStore.CreateInMemory();
        using TftpEventRecorder recorder = Recorder(store);

        recorder.RecordFault(new TftpFaultEventArgs(When, "the socket failed", "restart it", null, fatal));

        Assert.Equal(expected, Assert.Single(store.Events.All()).Severity);
    }

    /// <summary>
    /// The same path driven by a real datagram on a real socket - the only test here that would
    /// fail if the constructor stopped subscribing to anything.
    /// </summary>
    [Fact]
    public async Task ARequestOffTheWireLandsInTheProjectFile()
    {
        using ProjectStore store = ProjectStore.CreateInMemory();
        await using TftpWireHarness harness = await TftpWireHarness.StartAsync();

        using TftpEventRecorder recorder = new(store, harness.Server);

        // Subscribed after the recorder, so by the time this fires the recorder's handler has
        // already run: the watch invokes handlers in subscription order, on one thread.
        var recorded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Server.RequestReceived += (_, _) => recorded.TrySetResult();

        harness.Send(TftpFrames.PlainWriteRequest());
        await recorded.Task.WaitAsync(TimeSpan.FromSeconds(5));

        EventRecord row = Assert.Single(store.Events.All(), e => e.Category == EventCategory.Tftp
            && e.Message.Contains("FROM00.IMG", StringComparison.Ordinal));

        Assert.Contains("\"operation\":\"WRQ\"", row.Detail!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A database problem must never be able to stop the watch recording the next request, which
    /// on a bench session may be the only one anybody gets.
    /// </summary>
    [Fact]
    public async Task AFailedWriteIsCountedAndReportedRatherThanThrown()
    {
        ProjectStore store = ProjectStore.CreateInMemory();
        await using TftpWireHarness harness = await TftpWireHarness.StartAsync();

        using TftpEventRecorder recorder = new(store, harness.Server);

        var failed = new TaskCompletionSource<PersistenceFailedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        recorder.RecordingFailed += (_, e) => failed.TrySetResult(e);

        store.Dispose();
        harness.Send(TftpFrames.PlainWriteRequest());

        PersistenceFailedEventArgs failure = await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("request", failure.What);
        Assert.Equal(1, recorder.FailureCount);

        // The watch carried on: it still saw and reported the request.
        Assert.Equal("FROM00.IMG", (await harness.NextRequestAsync()).Request.FileName);
    }

    [Fact]
    public void StopsRecordingOnceDisposed()
    {
        using ProjectStore store = ProjectStore.CreateInMemory();
        TftpWatchServer server = Server();
        var recorder = new TftpEventRecorder(store, server);

        recorder.Dispose();
        recorder.Dispose();

        Assert.Equal(0, recorder.RecordedCount);
    }

    private static TftpEventRecorder Recorder(ProjectStore store) => new(store, Server());

    private static TftpWatchServer Server() =>
        new(new FakeNicInventory(), new TftpWatchOptions { ListenPort = 6969 });

    private static TftpRequestEventArgs RequestArgs(
        string fileName = "FROM00.IMG",
        TftpTransferMode mode = TftpTransferMode.Octet,
        string rawMode = "octet",
        TftpOptions? options = null,
        bool isRetransmit = false,
        TftpWatchAction action = TftpWatchAction.Refused,
        string reason = "recorded, and refused so the controller stops rather than retransmitting") =>
        new(
            When,
            new TftpRequestMessage(
                TftpOpcode.WriteRequest,
                fileName,
                Encoding.Latin1.GetBytes(fileName),
                mode,
                rawMode,
                options ?? TftpOptions.None),
            new IPEndPoint(IPAddress.Parse("192.168.1.51"), 50000),
            12,
            Nics.Healthy(),
            IPAddress.Parse("192.168.1.10"),
            isRetransmit,
            action,
            reason);
}
