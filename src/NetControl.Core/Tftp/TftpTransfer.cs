using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace NetControl.Core.Tftp;

/// <summary>
/// The two halves of a TFTP transfer once the handshake is over: sending DATA and waiting for each
/// ACK, and receiving DATA and acknowledging each block. Lockstep only - one block in flight -
/// because that is what every robot controller and every plant server speaks.
///
/// <para>Shared by the probe (which writes and then reads back, as a client) and by Accept mode
/// (which receives a write and serves a read, as a server), so the two can be tested against each
/// other over loopback and a mistake in one cannot be hidden by the same mistake in the other being
/// absent.</para>
///
/// <para>Three rules from RFC 1350 that are easy to get wrong and expensive when they are:</para>
/// <list type="bullet">
///   <item><b>A duplicate ACK is never answered with a retransmit.</b> Doing so is the "Sorcerer's
///         Apprentice" bug: every block goes out twice from then on, and the transfer doubles its
///         traffic without ever failing.</item>
///   <item><b>A packet from any other endpoint is refused with error 5 and otherwise ignored.</b>
///         Address and port together are the transfer identifier; a packet from elsewhere is not
///         part of this transfer, however plausible it looks.</item>
///   <item><b>What follows block 65535 is not specified.</b> This sends 0, which is the common
///         choice, and accepts either 0 or 1 when receiving, recording which one the other end used.</item>
/// </list>
///
/// <para>No span locals anywhere in here - these are async methods (CS4013). Parsing goes through
/// <see cref="TftpPacket.TryParse"/> with the span made at the call.</para>
/// </summary>
internal static class TftpTransfer
{
    /// <summary>Retries before a transfer is given up. A controller gives up sooner than this.</summary>
    internal const int DefaultMaxRetries = 5;

    /// <summary>A wait for one packet: what came, from where, or why nothing did.</summary>
    internal readonly record struct Arrival(TftpMessage? Message, IPEndPoint? From, bool TimedOut, bool Unreachable);

    /// <summary>
    /// Waits up to <paramref name="timeout"/> for a TFTP packet that <paramref name="accept"/> says
    /// belongs to this transfer. Anything else is answered with error 5 and the wait goes on.
    /// </summary>
    internal static async Task<Arrival> NextAsync(
        Socket socket,
        byte[] buffer,
        Func<IPEndPoint, bool> accept,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();

        while (true)
        {
            TimeSpan remaining = timeout - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
            {
                return new Arrival(null, null, TimedOut: true, Unreachable: false);
            }

            SocketReceiveFromResult result;
            using (var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                wait.CancelAfter(remaining);
                try
                {
                    result = await socket
                        .ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), wait.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return new Arrival(null, null, TimedOut: true, Unreachable: false);
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
                {
                    // Windows surfaces an ICMP port unreachable for an earlier send as a reset on the
                    // next receive. For UDP that means exactly one thing: nothing is listening there.
                    return new Arrival(null, null, TimedOut: false, Unreachable: true);
                }
            }

            if (result.RemoteEndPoint is not IPEndPoint from)
            {
                continue;
            }

            if (!accept(from))
            {
                SendQuietly(socket, TftpPacket.EncodeError(
                    TftpErrorCode.UnknownTransferId,
                    "Unknown transfer ID - this port belongs to another transfer."), from);
                continue;
            }

            if (!TftpPacket.TryParse(buffer.AsSpan(0, result.ReceivedBytes), out TftpMessage? message, out _)
                || message is null)
            {
                continue;
            }

            return new Arrival(message, from, TimedOut: false, Unreachable: false);
        }
    }

    /// <summary>
    /// Sends <paramref name="source"/> to <paramref name="peer"/> as DATA blocks 1, 2, ..., waiting for
    /// each ACK, and ends with a block shorter than <paramref name="blockSize"/> - an empty one when
    /// the length is an exact multiple.
    /// </summary>
    internal static async Task<TftpTransferOutcome> SendBlocksAsync(
        Socket socket,
        IPEndPoint peer,
        Stream source,
        int blockSize,
        TimeSpan timeout,
        int maxRetries,
        IncrementalHash? hash,
        Action<long>? progress,
        CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var receive = new byte[TftpPacket.MaxDatagramLength];
        var payload = new byte[blockSize];
        long absolute = 0;
        long bytes = 0;
        int retransmits = 0;
        int wraps = 0;

        TftpTransferStats Stats() =>
            new(bytes, absolute, blockSize, retransmits, clock.Elapsed, wraps, PeerRolledOverToOne: false);

        while (true)
        {
            int count = await FillAsync(source, payload, cancellationToken).ConfigureAwait(false);
            absolute++;
            ushort block = unchecked((ushort)absolute);
            if (block == 0)
            {
                wraps++;
            }

            hash?.AppendData(payload, 0, count);
            byte[] datagram = TftpPacket.EncodeData(block, payload.AsSpan(0, count));
            socket.SendTo(datagram, SocketFlags.None, peer);

            int attempts = 0;
            bool acknowledged = false;
            while (!acknowledged)
            {
                Arrival arrival = await NextAsync(socket, receive, peer.Equals, timeout, cancellationToken)
                    .ConfigureAwait(false);

                if (arrival.Unreachable)
                {
                    return new TftpTransferOutcome(
                        false,
                        Stats(),
                        $"{peer} closed its port at block {Number(absolute)}: the other end abandoned the "
                            + "transfer.",
                        "If it happens at the same block every time, see the rollover note; otherwise the "
                            + "other end timed out first - check for loss on the link.")
                    {
                        StalledAtBlock = absolute,
                        PeerUnreachable = true,
                    };
                }

                if (arrival.TimedOut)
                {
                    if (++attempts > maxRetries)
                    {
                        return Stalled(Stats(), peer, absolute, "an ACK");
                    }

                    retransmits++;
                    socket.SendTo(datagram, SocketFlags.None, peer);
                    continue;
                }

                switch (arrival.Message)
                {
                    case TftpAckMessage ack when ack.Block == block:
                        acknowledged = true;
                        break;

                    case TftpErrorMessage error:
                        return FromPeerError(Stats(), error, absolute);

                    default:
                        // A duplicate ACK for the previous block, or something unexpected. Never
                        // answered with a retransmit - see the remarks on the class.
                        break;
                }
            }

            bytes += count;
            progress?.Invoke(bytes);

            if (count < blockSize)
            {
                return new TftpTransferOutcome(true, Stats());
            }
        }
    }

    /// <summary>
    /// Receives DATA from <paramref name="peer"/> into <paramref name="sink"/>, acknowledging each
    /// block, until a short block ends it.
    /// </summary>
    /// <param name="opening">
    /// What to send again if block 1 does not arrive - the ACK 0 or OACK that accepted a write, or
    /// the ACK 0 that answered an OACK. Null when <paramref name="first"/> already holds block 1.
    /// </param>
    /// <param name="first">Block 1, when it arrived during the handshake.</param>
    internal static async Task<TftpTransferOutcome> ReceiveBlocksAsync(
        Socket socket,
        IPEndPoint peer,
        Stream sink,
        int blockSize,
        byte[]? opening,
        TftpDataMessage? first,
        TimeSpan timeout,
        int maxRetries,
        IncrementalHash? hash,
        Action<long>? progress,
        CancellationToken cancellationToken,
        bool dally = true)
    {
        var clock = Stopwatch.StartNew();
        var receive = new byte[TftpPacket.MaxDatagramLength];
        byte[] lastSent = opening ?? TftpPacket.EncodeAck(0);
        TftpDataMessage? pending = first;
        long absolute = 0;
        ushort lastBlock = 0;
        long bytes = 0;
        int retransmits = 0;
        int wraps = 0;
        bool rolledToOne = false;
        int attempts = 0;

        TftpTransferStats Stats() =>
            new(bytes, absolute, blockSize, retransmits, clock.Elapsed, wraps, rolledToOne);

        while (true)
        {
            TftpDataMessage data;
            if (pending is not null)
            {
                data = pending;
                pending = null;
            }
            else
            {
                Arrival arrival = await NextAsync(socket, receive, peer.Equals, timeout, cancellationToken)
                    .ConfigureAwait(false);

                if (arrival.Unreachable)
                {
                    return new TftpTransferOutcome(
                        false,
                        Stats(),
                        $"{peer} closed its port after block {Number(absolute)}: the sender abandoned the "
                            + "transfer.",
                        "The sender timed out waiting for an acknowledgement. Check for loss on the link.")
                    {
                        StalledAtBlock = absolute + 1,
                        PeerUnreachable = true,
                    };
                }

                if (arrival.TimedOut)
                {
                    if (++attempts > maxRetries)
                    {
                        return Stalled(Stats(), peer, absolute + 1, "DATA");
                    }

                    retransmits++;
                    socket.SendTo(lastSent, SocketFlags.None, peer);
                    continue;
                }

                if (arrival.Message is TftpErrorMessage error)
                {
                    return FromPeerError(Stats(), error, absolute + 1);
                }

                if (arrival.Message is not TftpDataMessage received)
                {
                    continue;
                }

                data = received;
            }

            ushort expected = unchecked((ushort)(lastBlock + 1));
            bool isNext = data.Block == expected;
            bool wrapsToOne = !isNext && absolute > 0 && lastBlock == ushort.MaxValue && data.Block == 1;

            if (!isNext && !wrapsToOne)
            {
                // The block we already have: our ACK was lost, so send it again. Anything else is
                // out of order and is left for the sender's own timeout to sort out.
                if (absolute > 0 && data.Block == lastBlock)
                {
                    socket.SendTo(lastSent, SocketFlags.None, peer);
                }

                continue;
            }

            if (data.Data.Length > blockSize)
            {
                SendQuietly(socket, TftpPacket.EncodeError(
                    TftpErrorCode.IllegalOperation,
                    "Block larger than the negotiated block size."), peer);
                return new TftpTransferOutcome(
                    false,
                    Stats(),
                    $"{peer} sent {Number(data.Data.Length)} bytes in one block when {Number(blockSize)} was "
                        + "agreed.",
                    "The two ends disagree about the block size - one of them is ignoring the option "
                        + "negotiation.")
                {
                    StalledAtBlock = absolute + 1,
                };
            }

            attempts = 0;
            if (lastBlock == ushort.MaxValue)
            {
                wraps++;
            }

            if (wrapsToOne)
            {
                rolledToOne = true;
            }

            absolute++;
            lastBlock = data.Block;

            await sink.WriteAsync(data.Data, 0, data.Data.Length, cancellationToken).ConfigureAwait(false);
            hash?.AppendData(data.Data);
            bytes += data.Data.Length;
            progress?.Invoke(bytes);

            lastSent = TftpPacket.EncodeAck(data.Block);
            socket.SendTo(lastSent, SocketFlags.None, peer);

            if (data.Data.Length < blockSize)
            {
                // Dally: if our last ACK was lost, the sender sends the last block again and is
                // waiting for an answer that will otherwise never come. A server that has a file to
                // close first passes false and dallies itself afterwards - see TftpWatchServer.
                if (dally)
                {
                    await DallyAsync(socket, peer, timeout, cancellationToken).ConfigureAwait(false);
                }

                return new TftpTransferOutcome(true, Stats());
            }
        }
    }

    /// <summary>Sends without letting a failure escape - for error packets, which are best effort.</summary>
    internal static void SendQuietly(Socket socket, byte[] datagram, EndPoint to)
    {
        try
        {
            socket.SendTo(datagram, SocketFlags.None, to);
        }
        catch (SocketException)
        {
            // An error packet that cannot be sent changes nothing about what is reported.
        }
        catch (ObjectDisposedException)
        {
            // The transfer is already over.
        }
    }

    internal static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>
    /// After the last block of a receive: wait one timeout for the sender to send it again - which
    /// it does only if our final ACK was lost - and acknowledge it if it does.
    /// </summary>
    internal static async Task DallyAsync(
        Socket socket,
        IPEndPoint peer,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[TftpPacket.MaxDatagramLength];
        Arrival arrival = await NextAsync(socket, buffer, peer.Equals, timeout, cancellationToken)
            .ConfigureAwait(false);

        if (arrival.Message is TftpDataMessage again)
        {
            SendQuietly(socket, TftpPacket.EncodeAck(again.Block), peer);
        }
    }

    private static async Task<int> FillAsync(Stream source, byte[] buffer, CancellationToken cancellationToken)
    {
        int filled = 0;
        while (filled < buffer.Length)
        {
            int read = await source
                .ReadAsync(buffer.AsMemory(filled, buffer.Length - filled), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            filled += read;
        }

        return filled;
    }

    private static TftpTransferOutcome Stalled(TftpTransferStats stats, IPEndPoint peer, long block, string waitingFor)
    {
        bool atRollover = block % (TftpLimits.LastBlockNumber + 1L) == 0;
        string where = $"block {Number(block)}";

        return new TftpTransferOutcome(
            false,
            stats,
            atRollover
                ? $"The transfer stopped at {where} - exactly where the 16-bit block counter rolls over. "
                    + $"{peer} stopped answering after block 65,535."
                : $"The transfer stopped at {where}: no {waitingFor} came back from {peer} after "
                    + "repeated retries.",
            atRollover
                ? "The two ends disagree about what follows block 65,535, or one of them does not roll "
                    + "over at all. Negotiate a bigger block size so the file needs fewer than 65,536 "
                    + "blocks, or set the server to roll over to 0."
                : "Packets are being lost between here and there. If the transfer never gets past the "
                    + "first block, a firewall is passing UDP/69 but dropping the transfer's own ports.")
        {
            StalledAtBlock = block,
        };
    }

    private static TftpTransferOutcome FromPeerError(TftpTransferStats stats, TftpErrorMessage error, long block)
    {
        (string summary, string? remediation) = error.Explain();
        return new TftpTransferOutcome(
            false,
            stats,
            $"The other end stopped the transfer at block {Number(block)}: {summary}",
            remediation)
        {
            PeerError = error,
            StalledAtBlock = block,
        };
    }
}
