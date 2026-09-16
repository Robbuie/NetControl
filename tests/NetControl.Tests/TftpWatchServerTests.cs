using System.Net;
using NetControl.Core.Tftp;
using Xunit;

namespace NetControl.Tests;

/// <summary>
/// The watch, on a real socket over loopback. No test broadcasts, and none binds UDP/69: the
/// machine running these is quite likely to be the one with the backup server on it.
/// </summary>
public class TftpWatchServerTests
{
    private static readonly TimeSpan Silence = TimeSpan.FromMilliseconds(400);

    [Fact]
    public async Task RecordsAWriteRequestWithTheFilenameExactlyAsSent()
    {
        // The whole reason the watch exists. A server that refuses a request logs its own
        // decision, never the name it refused.
        await using TftpWireHarness harness = await TftpWireHarness.StartAsync();

        harness.Send(TftpFrames.Request((ushort)TftpOpcode.WriteRequest, @"\md:\FROM00.IMG", "octet"));

        TftpRequestEventArgs seen = await harness.NextRequestAsync();

        Assert.True(seen.Request.IsWrite);
        Assert.Equal(@"\md:\FROM00.IMG", seen.Request.FileName);
        Assert.Equal(TftpTransferMode.Octet, seen.Request.Mode);
        Assert.False(seen.IsRetransmit);
    }

    [Fact]
    public async Task RecordsTheSourcePortBecauseThatIsTheClientsTransferIdentifier()
    {
        await using TftpWireHarness harness = await TftpWireHarness.StartAsync();

        harness.Send(TftpFrames.PlainWriteRequest());

        TftpRequestEventArgs seen = await harness.NextRequestAsync();

        Assert.Equal(harness.ClientPort, seen.Source.Port);
    }

    [Fact]
    public async Task RecordsTheOptionsOfferedSoTheGapAgainstWhatAServerGrantsIsVisible()
    {
        await using TftpWireHarness harness = await TftpWireHarness.StartAsync();

        harness.Send(TftpFrames.NegotiatingWriteRequest());

        TftpRequestEventArgs seen = await harness.NextRequestAsync();

        Assert.Equal(1468, seen.Request.Options.BlockSize);
        Assert.Equal(134217728L, seen.Request.Options.TransferSize);
    }

    [Fact]
    public async Task RefusesAnObservedRequestSoTheControllerStopsRatherThanRetransmitting()
    {
        // TFTP has no other way to say no. Silence reads to a client as a lost packet, so it
        // retransmits until it times out and somebody watches a progress bar that never moves.
        await using TftpWireHarness harness = await TftpWireHarness.StartAsync();

        harness.Send(TftpFrames.PlainWriteRequest());

        TftpRequestEventArgs seen = await harness.NextRequestAsync();
        Assert.Equal(TftpWatchAction.Refused, seen.Action);

        byte[]? answer = await harness.ReceiveAsClientAsync();
        Assert.NotNull(answer);

        Assert.True(TftpPacket.TryParse(answer, out TftpMessage? message, out _));
        var error = Assert.IsType<TftpErrorMessage>(message);
        Assert.Contains("not serving", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnswersFromTheWellKnownPortByDefault()
    {
        // What servers in the field do for an immediate rejection, and what the watch has always
        // done. Asserted so that changing the default is a deliberate act with a test to update
        // rather than something that drifts.
        await using TftpWireHarness harness = await TftpWireHarness.StartAsync();

        harness.Send(TftpFrames.PlainWriteRequest());

        TftpRequestEventArgs seen = await harness.NextRequestAsync();
        (byte[]? answer, IPEndPoint? from) = await harness.ReceiveWithSourceAsync();

        Assert.NotNull(answer);
        Assert.NotNull(from);
        Assert.Equal(harness.Options.ListenPort, from!.Port);
        Assert.Equal(harness.Options.ListenPort, seen.RefusalSourcePort);
    }

    [Fact]
    public async Task AnswersFromAFreshTransferIdentifierWhenAskedTo()
    {
        // The other half of the open question. No FANUC has been seen to accept either kind of
        // refusal, and finding out costs a robot in its boot monitor - so both behaviours are
        // reachable from one visit, and the log row says which one was tried.
        await using TftpWireHarness harness =
            await TftpWireHarness.StartAsync(refusalSource: TftpRefusalSource.TemporarySocket);

        harness.Send(TftpFrames.PlainWriteRequest());

        TftpRequestEventArgs seen = await harness.NextRequestAsync();
        (byte[]? answer, IPEndPoint? from) = await harness.ReceiveWithSourceAsync();

        Assert.NotNull(answer);
        Assert.NotNull(from);
        Assert.NotEqual(harness.Options.ListenPort, from!.Port);
        Assert.Equal(from.Port, seen.RefusalSourcePort);
        Assert.Equal(TftpWatchAction.Refused, seen.Action);

        // Still a refusal, and still one somebody at a pendant can read. A different socket must
        // not quietly become a different answer.
        Assert.True(TftpPacket.TryParse(answer, out TftpMessage? message, out _));
        var error = Assert.IsType<TftpErrorMessage>(message);
        Assert.Contains("not serving", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MarksARepeatFromTheSameSourceAsARetransmitAndStillAnswersIt()
    {
        // The DHCP side's rule, for the same reason: a client retransmitting is a client that did
        // not get the last answer, so it gets answered again. Only the log entry is marked.
        await using TftpWireHarness harness = await TftpWireHarness.StartAsync();

        harness.Send(TftpFrames.PlainWriteRequest());
        TftpRequestEventArgs first = await harness.NextRequestAsync();
        Assert.False(first.IsRetransmit);
        Assert.NotNull(await harness.ReceiveAsClientAsync());

        harness.Send(TftpFrames.PlainWriteRequest());
        TftpRequestEventArgs second = await harness.NextRequestAsync();

        Assert.True(second.IsRetransmit);
        Assert.Equal(TftpWatchAction.Refused, second.Action);
        Assert.NotNull(await harness.ReceiveAsClientAsync());
    }

    [Fact]
    public async Task RecordsARequestOnAnUnselectedAdapterAndTransmitsNothing()
    {
        // Reported rather than dropped - "your controller is asking, but on an adapter you did not
        // pick" is one of the most useful sentences this tool produces. Never answered, though:
        // a refusal is a transmission, and this tool does not transmit onto a segment nobody chose.
        await using TftpWireHarness harness = await TftpWireHarness.StartAsync(interfaceIndexFilter: 9999);

        harness.Send(TftpFrames.PlainWriteRequest());

        TftpRequestEventArgs seen = await harness.NextRequestAsync();

        Assert.Equal(TftpWatchAction.Ignored, seen.Action);
        Assert.Contains("not the selected adapter", seen.Reason, StringComparison.Ordinal);
        Assert.Null(await harness.ReceiveAsClientAsync(Silence));
    }

    [Fact]
    public async Task TransmitsNothingAtAllWhenRefusalsAreTurnedOff()
    {
        await using TftpWireHarness harness = await TftpWireHarness.StartAsync(sendRefusal: false);

        harness.Send(TftpFrames.PlainWriteRequest());

        TftpRequestEventArgs seen = await harness.NextRequestAsync();

        Assert.Equal(TftpWatchAction.Ignored, seen.Action);
        Assert.Null(await harness.ReceiveAsClientAsync(Silence));

        // Nothing was sent, so there is no port to name. A row claiming one would be inventing
        // evidence about the exact question the refusal source exists to answer.
        Assert.Null(seen.RefusalSourcePort);
    }

    [Fact]
    public async Task ReportsADatagramThatIsNotTftpRatherThanDiscardingItQuietly()
    {
        // Unlike UDP/67, this port does not see much unrelated traffic, so something else arriving
        // here is a finding.
        await using TftpWireHarness harness = await TftpWireHarness.StartAsync();

        harness.Send([0xDE, 0xAD, 0xBE, 0xEF]);

        TftpFaultEventArgs fault = await harness.NextFaultAsync();

        Assert.False(fault.IsFatal);
        Assert.Contains("could not be read as TFTP", fault.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportsMidTransferTrafficArrivingOnTheWellKnownPort()
    {
        // A transfer moves off port 69 after its first packet. Traffic still arriving here means
        // one end is addressing the wrong port, which is what a NAT or firewall rewriting the
        // ephemeral data port looks like from this side - and one of the few ways to see it at all
        // without a capture.
        await using TftpWireHarness harness = await TftpWireHarness.StartAsync();

        harness.Send(TftpPacket.EncodeAck(1));

        TftpFaultEventArgs fault = await harness.NextFaultAsync();

        Assert.Contains("ACK block 1", fault.Message, StringComparison.Ordinal);
        Assert.NotNull(fault.Remediation);
        Assert.Contains("TFTP helper", fault.Remediation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExplainsAnErrorSentToTheWellKnownPortInItsOwnWords()
    {
        await using TftpWireHarness harness = await TftpWireHarness.StartAsync();

        harness.Send(TftpPacket.EncodeError(TftpErrorCode.UnknownTransferId, "bad TID"));

        TftpFaultEventArgs fault = await harness.NextFaultAsync();

        Assert.Contains("ephemeral data port", fault.Message, StringComparison.Ordinal);
        Assert.Contains("bad TID", fault.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefusesToStartInAcceptModeRatherThanQuietlyObserving()
    {
        // A tool that reports itself as accepting backups while refusing every one of them would
        // be worse than no tool. Accept is declared because the distinction shapes the type; it is
        // not built, and saying so out loud is the whole point.
        var server = new TftpWatchServer(
            new FakeNicInventory(),
            new TftpWatchOptions { Mode = TftpWatchMode.Accept, ListenPort = 0 });

        TftpBindException thrown = await Assert.ThrowsAsync<TftpBindException>(() => server.RunAsync());

        Assert.Contains("not built", thrown.Message, StringComparison.Ordinal);
        Assert.NotNull(thrown.Remediation);
        Assert.False(server.IsListening);
    }

    [Fact]
    public async Task RefusesToRunTwiceAtOnce()
    {
        await using TftpWireHarness harness = await TftpWireHarness.StartAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Server.RunAsync());
    }

    [Fact]
    public async Task ReadsRequestsThatAreLargerThanADefaultBlock()
    {
        // The receive buffer is sized from TftpPacket.MaxDatagramLength rather than from a guess.
        // A buffer that truncates does not fail - a short read looks like a shorter request, and
        // the filename comes back wrong.
        await using TftpWireHarness harness = await TftpWireHarness.StartAsync();

        string longName = new('a', 900);
        harness.Send(TftpFrames.Request((ushort)TftpOpcode.WriteRequest, longName, "octet"));

        TftpRequestEventArgs seen = await harness.NextRequestAsync();

        Assert.Equal(longName, seen.Request.FileName);
    }
}
