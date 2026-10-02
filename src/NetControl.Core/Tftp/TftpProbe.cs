using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace NetControl.Core.Tftp;

/// <summary>
/// PLAN-TFTP.md part E4: be the client, against the real server. Write a file of a known size,
/// read it back, and compare - from the PC the backup leaves from, ideally a laptop on the robot
/// network, which is the controller's vantage.
///
/// <para>It answers what watching cannot: whether the server accepts a write at all, which address
/// and port it answers from, which options it grants against which it was asked for, whether the
/// transfer's own ports get through the firewall, and - with the default size - whether the block
/// counter's rollover survives the round trip.</para>
///
/// <para>Safety rules, from PLAN-TFTP.md: one file, the name the user typed, one transfer at a time,
/// and <b>nothing is ever deleted</b>. The result names the file it left.</para>
/// </summary>
public static class TftpProbe
{
    private const long ProgressStepBytes = 1024 * 1024;

    private static readonly TimeSpan ReadBackPause = TimeSpan.FromMilliseconds(500);

    public static async Task<TftpProbeResult> RunAsync(
        TftpProbeOptions options,
        IProgress<TftpProbeProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.FileName);
        ArgumentOutOfRangeException.ThrowIfNegative(options.SizeBytes);

        var findings = new List<string>();
        var server = new IPEndPoint(options.Server, options.ServerPort);
        TftpOptions requested = Requested(options);
        string size = Bytes(options.SizeBytes);
        string leftNote = $"The test file '{options.FileName}' is left on the server - the probe never deletes "
            + "anything. Remove it by hand when you are done.";

        progress?.Report(new TftpProbeProgress("Asking", 0, options.SizeBytes));

        using Socket socket = NewSocket(options.LocalAddress);
        byte[] request = TftpPacket.EncodeRequest(
            TftpOpcode.WriteRequest,
            options.FileName,
            TftpTransferMode.Octet,
            requested);

        // --- Handshake: the write request, and whatever answers it. ---------------------------
        var clock = Stopwatch.StartNew();
        var buffer = new byte[TftpPacket.MaxDatagramLength];
        socket.SendTo(request, SocketFlags.None, server);

        TftpTransfer.Arrival answer = default;
        for (int attempt = 0; ; attempt++)
        {
            // The answer comes from a new port by design, and on a multi-homed server possibly from a
            // different address - which is a finding, not a reason to ignore it.
            answer = await TftpTransfer.NextAsync(socket, buffer, _ => true, options.Timeout, cancellationToken)
                .ConfigureAwait(false);

            if (answer.Unreachable)
            {
                return Failed(
                    options,
                    requested,
                    findings,
                    $"Nothing is listening on UDP/{options.ServerPort.ToString(CultureInfo.InvariantCulture)} at "
                        + $"{options.Server}: Windows reported the port closed.",
                    "The TFTP server is not running on that machine, or it is listening on another address. "
                        + "Start it, and check the address set on the controller is the one it listens on.");
            }

            if (!answer.TimedOut && answer.Message is TftpAckMessage or TftpOptionAckMessage or TftpErrorMessage)
            {
                break;
            }

            if (answer.TimedOut)
            {
                if (attempt >= options.MaxRetries)
                {
                    return Failed(
                        options,
                        requested,
                        findings,
                        $"No answer from {server} to a write request, after "
                            + $"{(options.MaxRetries + 1).ToString(CultureInfo.InvariantCulture)} tries.",
                        "Either nothing is running there or something between here and there drops UDP/69. "
                            + "Check the address, that the server is running, and the server PC's inbound "
                            + "firewall rule for UDP/69. This is what a robot sees as a backup that never starts.");
                }

                socket.SendTo(request, SocketFlags.None, server);
            }
        }

        TimeSpan firstResponse = clock.Elapsed;
        IPEndPoint peer = answer.From!;
        findings.Add($"The server answered in {Milliseconds(firstResponse)}, from {peer}.");

        if (!peer.Address.Equals(options.Server))
        {
            findings.Add(
                $"The answer came from {peer.Address}, not from {options.Server}, the address it was asked on. "
                + "A strict TFTP client - which a robot controller is - discards that as an unknown sender, "
                + "and it looks exactly like silence. The server has more than one address: set it to answer "
                + "on the robot network's address only.");
        }

        if (answer.Message is TftpErrorMessage refused)
        {
            (string summary, string? remediation) = refused.Explain();
            findings.Add($"The server refused the write: {summary}");
            return Failed(options, requested, findings, $"The server refused to accept '{options.FileName}': {summary}", remediation)
                with
                {
                    AnsweredFrom = peer,
                    FirstResponse = firstResponse,
                };
        }

        TftpOptions granted = answer.Message is TftpOptionAckMessage oack ? oack.Options : TftpOptions.None;
        int blockSize = granted.BlockSize is { } agreed && TftpLimits.IsBlockSizeInRange(agreed)
            ? agreed
            : TftpLimits.DefaultBlockSize;

        if (!requested.IsEmpty && granted.IsEmpty)
        {
            findings.Add("The server ignored every option offered (it answered with ACK 0, not OACK), so the "
                + "transfer runs as plain RFC 1350 at 512-byte blocks.");
        }

        foreach (string difference in TftpOptions.DescribeDifferences(requested, granted))
        {
            findings.Add(difference);
        }

        if (TftpLimits.DescribeRolloverRisk(options.SizeBytes, blockSize) is { } risk)
        {
            findings.Add(risk + " This probe crosses that point on purpose.");
        }

        // --- Write. ------------------------------------------------------------------------------
        TftpTransferOutcome write;
        byte[] written;
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        using (var pattern = new TftpProbePattern(options.SizeBytes))
        {
            write = await TftpTransfer.SendBlocksAsync(
                    socket,
                    peer,
                    pattern,
                    blockSize,
                    options.Timeout,
                    options.MaxRetries,
                    hash,
                    Throttle("Writing", options.SizeBytes, progress),
                    cancellationToken)
                .ConfigureAwait(false);
            written = hash.GetHashAndReset();
        }

        if (!write.Succeeded)
        {
            findings.Add($"The write stopped after {write.Stats.Describe()}.");
            if (write.Stats.Blocks <= 1 && write.PeerError is null)
            {
                findings.Add("It never got past the first block. The write request on UDP/69 got through and "
                    + "the transfer's own port did not - the signature of a firewall or NAT that passes "
                    + "UDP/69 but not the ephemeral ports TFTP moves to.");
            }

            findings.Add(leftNote);
            return new TftpProbeResult
            {
                Succeeded = false,
                Summary = write.Failure ?? "The write did not finish.",
                Remediation = write.Remediation,
                Findings = findings,
                FileName = options.FileName,
                WriteAccepted = true,
                AnsweredFrom = peer,
                FirstResponse = firstResponse,
                Requested = requested,
                Granted = granted,
                Write = write,
            };
        }

        findings.Add($"Write: {write.Stats.Describe()}.");
        progress?.Report(new TftpProbeProgress("Writing", options.SizeBytes, options.SizeBytes));

        // --- Read back. --------------------------------------------------------------------------
        TftpTransferOutcome? read = null;
        bool? matched = null;
        string? readProblem = null;
        string? readRemediation = null;

        if (options.ReadBack)
        {
            // A moment for the server to close and rename the file before it is asked for. A real
            // server may refuse a read of a file it is still finishing, and that is not the finding.
            await Task.Delay(ReadBackPause, cancellationToken).ConfigureAwait(false);

            (read, matched, readProblem, readRemediation) = await ReadBackAsync(
                    options, blockSize, written, findings, progress, cancellationToken)
                .ConfigureAwait(false);
        }

        findings.Add(leftNote);

        bool succeeded = !options.ReadBack || matched == true;
        string summary;
        string? remediationText;

        if (!options.ReadBack)
        {
            summary = $"The server accepted a {size} write from this PC and acknowledged every block.";
            remediationText = null;
        }
        else if (matched == true)
        {
            string wrapped = write.Stats.Wraps > 0 ? ", including the block counter rolling over" : string.Empty;
            summary = $"The server took a {size} write and gave back an identical copy{wrapped}. This server, "
                + "and the path from this PC to it, handle a backup of that size.";
            remediationText = null;
        }
        else
        {
            summary = readProblem ?? "The file read back did not match what was written.";
            remediationText = readRemediation;
        }

        return new TftpProbeResult
        {
            Succeeded = succeeded,
            Summary = summary,
            Remediation = remediationText,
            Findings = findings,
            FileName = options.FileName,
            WriteAccepted = true,
            AnsweredFrom = peer,
            FirstResponse = firstResponse,
            Requested = requested,
            Granted = granted,
            Write = write,
            Read = read,
            ReadBackMatched = matched,
        };
    }

    private static async Task<(TftpTransferOutcome? Read, bool? Matched, string? Problem, string? Remediation)>
        ReadBackAsync(
            TftpProbeOptions options,
            int blockSize,
            byte[] written,
            List<string> findings,
            IProgress<TftpProbeProgress>? progress,
            CancellationToken cancellationToken)
    {
        // A fresh socket, so the read is its own transfer with its own identifier, as a restore would be.
        using Socket socket = NewSocket(options.LocalAddress);
        var server = new IPEndPoint(options.Server, options.ServerPort);
        var buffer = new byte[TftpPacket.MaxDatagramLength];

        var offered = new List<TftpOption> { new(TftpOption.TransferSizeName, "0") };
        if (blockSize != TftpLimits.DefaultBlockSize)
        {
            offered.Add(new TftpOption(TftpOption.BlockSizeName, blockSize.ToString(CultureInfo.InvariantCulture)));
        }

        byte[] request = TftpPacket.EncodeRequest(
            TftpOpcode.ReadRequest,
            options.FileName,
            TftpTransferMode.Octet,
            new TftpOptions(offered));

        progress?.Report(new TftpProbeProgress("Reading back", 0, options.SizeBytes));
        socket.SendTo(request, SocketFlags.None, server);

        TftpTransfer.Arrival answer = default;
        for (int attempt = 0; ; attempt++)
        {
            answer = await TftpTransfer.NextAsync(socket, buffer, _ => true, options.Timeout, cancellationToken)
                .ConfigureAwait(false);

            if (answer.Unreachable || (answer.TimedOut && attempt >= options.MaxRetries))
            {
                return (null, false,
                    "The write worked, but the server did not answer a request to read the file back.",
                    "Some servers are configured write-only. The backup itself may be fine; a restore from "
                        + "this server would not be.");
            }

            if (answer.TimedOut)
            {
                socket.SendTo(request, SocketFlags.None, server);
                continue;
            }

            if (answer.Message is TftpDataMessage or TftpOptionAckMessage or TftpErrorMessage)
            {
                break;
            }
        }

        IPEndPoint peer = answer.From!;

        if (answer.Message is TftpErrorMessage error)
        {
            (string summary, string? remediation) = error.Explain();
            return (null, false,
                $"The write worked, but the server refused to send the file back: {summary}",
                remediation ?? "The server may be write-only, or read from a different folder than it writes to.");
        }

        int readBlockSize = blockSize;
        byte[]? opening = null;
        TftpDataMessage? first = null;

        if (answer.Message is TftpOptionAckMessage oack)
        {
            readBlockSize = oack.Options.BlockSize is { } agreed && TftpLimits.IsBlockSizeInRange(agreed)
                ? agreed
                : TftpLimits.DefaultBlockSize;
            opening = TftpPacket.EncodeAck(0);
            socket.SendTo(opening, SocketFlags.None, peer);

            if (oack.Options.TransferSize is { } reported && reported != options.SizeBytes)
            {
                findings.Add($"The server reports the file as {Bytes(reported)}, not the {Bytes(options.SizeBytes)} "
                    + "written. It did not keep all of it.");
            }
        }
        else
        {
            first = (TftpDataMessage)answer.Message!;

            // Without an OACK the server runs at 512 whatever was asked for.
            readBlockSize = TftpLimits.DefaultBlockSize;
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        TftpTransferOutcome read = await TftpTransfer.ReceiveBlocksAsync(
                socket,
                peer,
                Stream.Null,
                readBlockSize,
                opening,
                first,
                options.Timeout,
                options.MaxRetries,
                hash,
                Throttle("Reading back", options.SizeBytes, progress),
                cancellationToken)
            .ConfigureAwait(false);

        if (!read.Succeeded)
        {
            findings.Add($"The read-back stopped after {read.Stats.Describe()}.");
            return (read, false,
                $"The write worked, but reading it back failed: {read.Failure}",
                read.Remediation);
        }

        findings.Add($"Read back: {read.Stats.Describe()}.");
        byte[] readHash = hash.GetHashAndReset();

        if (read.Stats.Bytes != options.SizeBytes)
        {
            return (read, false,
                $"The file came back {Bytes(read.Stats.Bytes)} long, not the {Bytes(options.SizeBytes)} written.",
                "The server truncated or padded it. If the length is a multiple of 33,553,920 short, the "
                    + "block counter rollover lost data.");
        }

        if (!CryptographicOperations.FixedTimeEquals(readHash, written))
        {
            return (read, false,
                "The file came back the right length but with different contents.",
                read.Stats.Wraps > 0 || read.Stats.PeerRolledOverToOne
                    ? "The block counter rolled over during the transfer; the two ends disagree about what "
                        + "follows block 65,535. A backup this size would be silently corrupted."
                    : "Something between here and the server is altering the data - a netascii translation, "
                        + "or a proxy rewriting the payload.");
        }

        return (read, true, null, null);
    }

    private static TftpOptions Requested(TftpProbeOptions options)
    {
        var offered = new List<TftpOption>
        {
            new(TftpOption.TransferSizeName, options.SizeBytes.ToString(CultureInfo.InvariantCulture)),
        };

        if (options.BlockSize is { } blockSize)
        {
            offered.Add(new TftpOption(TftpOption.BlockSizeName, blockSize.ToString(CultureInfo.InvariantCulture)));
        }

        return new TftpOptions(offered);
    }

    private static Socket NewSocket(IPAddress? local)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.Bind(new IPEndPoint(local ?? IPAddress.Any, 0));
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static Action<long>? Throttle(string stage, long total, IProgress<TftpProbeProgress>? progress)
    {
        if (progress is null)
        {
            return null;
        }

        IProgress<TftpProbeProgress> sink = progress;
        long next = 0;
        return done =>
        {
            if (done >= next || done >= total)
            {
                next = done + ProgressStepBytes;
                sink.Report(new TftpProbeProgress(stage, done, total));
            }
        };
    }

    private static TftpProbeResult Failed(
        TftpProbeOptions options,
        TftpOptions requested,
        List<string> findings,
        string summary,
        string? remediation) => new()
        {
            Succeeded = false,
            Summary = summary,
            Remediation = remediation,
            Findings = findings,
            FileName = options.FileName,
            Requested = requested,
        };

    private static string Bytes(long bytes) =>
        bytes >= 1024 * 1024
            ? (bytes / (1024.0 * 1024.0)).ToString("N1", CultureInfo.InvariantCulture) + " MB"
            : bytes.ToString("N0", CultureInfo.InvariantCulture) + " bytes";

    private static string Milliseconds(TimeSpan elapsed) =>
        elapsed.TotalMilliseconds.ToString("N0", CultureInfo.InvariantCulture) + " ms";
}
