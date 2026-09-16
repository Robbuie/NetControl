using System.Globalization;
using System.Net;
using System.Net.Sockets;
using NetControl.Core.Interfaces;

namespace NetControl.Core.Tftp;

/// <summary>
/// The UDP/69 watcher: what asked for what, from where, and on which adapter.
///
/// <para>This is the direct analogue of <c>DhcpServer</c> in Watch mode, and it exists to answer
/// one question that nothing else on a plant laptop can: <b>did the controller ever send a write
/// request, and what exactly did it ask for?</b> A TFTP server that refuses a request logs its own
/// decision and not the name it refused, so a controller asking for a path with a drive prefix or
/// a leading separator the server rejects currently costs an afternoon. The first packet of every
/// transfer carries the filename, the mode and the options; recording it is the whole job.</para>
///
/// <para>It binds 0.0.0.0:69 once with IP_PKTINFO on and attributes each datagram to its arrival
/// adapter in software, for the same reason the DHCP side does: binding per adapter is what makes
/// a tool miss the request that explains everything.</para>
///
/// <para><b>It transfers nothing.</b> The only thing it ever transmits is a TFTP error refusing a
/// request, unicast to whoever just asked. Accepting a transfer is
/// <see cref="TftpWatchMode.Accept"/> and is not built; starting in that mode is refused rather
/// than quietly downgraded.</para>
///
/// <para>No UI dependency, and no console. Handlers run on the receive loop's thread, so a UI must
/// marshal and no handler should block.</para>
/// </summary>
public sealed class TftpWatchServer
{
    private readonly INicInventory _nics;
    private readonly TftpWatchOptions _options;
    private readonly TftpRetransmitFilter _retransmits;
    private readonly TimeProvider _time;
    private int _running;

    public TftpWatchServer(
        INicInventory nics,
        TftpWatchOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(nics);

        _nics = nics;
        _options = options ?? new TftpWatchOptions();
        _time = timeProvider ?? TimeProvider.System;
        _retransmits = new TftpRetransmitFilter(_options.RetransmitWindow, _time);
    }

    /// <summary>Every read or write request that arrived, whether or not it was answered.</summary>
    public event EventHandler<TftpRequestEventArgs>? RequestReceived;

    /// <summary>Anything else the user needs to know about, fatal or not.</summary>
    public event EventHandler<TftpFaultEventArgs>? Fault;

    /// <summary>Raised once the socket is bound and the loop is about to start receiving.</summary>
    public event EventHandler? Listening;

    public bool IsListening { get; private set; }

    public IPEndPoint? LocalEndPoint { get; private set; }

    public TftpWatchOptions Options => _options;

    /// <summary>
    /// Binds and receives until cancelled. Throws <see cref="TftpBindException"/> if it cannot
    /// take the port, or if something else holds it in a way that makes delivery ambiguous.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            throw new InvalidOperationException("This TftpWatchServer is already running.");
        }

        try
        {
            if (_options.Mode == TftpWatchMode.Accept)
            {
                // Refuse rather than behave like Observe. A tool that reports itself as accepting
                // backups while quietly refusing every one of them is worse than no tool at all.
                throw new TftpBindException(
                    "Accept mode is not built: this watch can record and refuse a transfer, but it "
                    + "cannot receive one.")
                {
                    Remediation = "Run in Observe mode to record what the controller asks for, and leave "
                        + "the real TFTP server to do the transfer.",
                };
            }

            PortConflictReport conflict = PortConflictDetector.Inspect(_options.ListenPort);

            if (conflict.Severity == PortConflictSeverity.Serious && _options.RefuseOnSeriousPortConflict)
            {
                throw new TftpBindException(ExplainConflict(conflict), conflict)
                {
                    Remediation = conflict.Remediation
                        ?? "Stop the TFTP server deliberately before watching this port, and start it again "
                        + "afterwards.",
                };
            }

            if (!conflict.IsClear)
            {
                RaiseFault(conflict.Summary, conflict.Remediation);
            }

            using Socket socket = Bind(conflict);

            LocalEndPoint = socket.LocalEndPoint as IPEndPoint;
            IsListening = true;
            Listening?.Invoke(this, EventArgs.Empty);

            await ReceiveLoopAsync(socket, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            IsListening = false;
            LocalEndPoint = null;
            Interlocked.Exchange(ref _running, 0);
        }
    }

    /// <summary>
    /// The refusal wording, which is different in kind from the DHCP side's. There, something else
    /// on UDP/67 is an intruder. Here, the process holding UDP/69 is almost certainly the backup
    /// server doing its job, and the sentence has to say so rather than reading like an accusation.
    /// </summary>
    private static string ExplainConflict(PortConflictReport conflict) =>
        $"{conflict.Summary} On a backup server that is expected: the process holding UDP/{conflict.Port} "
        + "is the TFTP server itself. Watching alongside it would let Windows deliver each request to "
        + "whichever socket it chose, so some backups would run and some would hit this watch.";

    private Socket Bind(PortConflictReport conflict)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

            // Same line that matters on the DHCP side: ask the stack which interface each datagram
            // arrived on rather than inferring it from the bind address.
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);

            socket.Bind(new IPEndPoint(IPAddress.Any, _options.ListenPort));
            return socket;
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            throw Explain(ex, conflict);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private TftpBindException Explain(SocketException ex, PortConflictReport conflict)
    {
        int port = _options.ListenPort;

        return ex.SocketErrorCode switch
        {
            SocketError.AccessDenied => new TftpBindException(
                $"Windows refused the bind to 0.0.0.0:{port} (access denied). Low ports are not reserved "
                    + "on Windows, so this is normally a port exclusion range rather than a permissions "
                    + "problem - Hyper-V and WSL both reserve blocks of UDP ports.",
                conflict,
                ex)
            {
                Remediation = "Run: netsh int ipv4 show excludedportrange protocol=udp",
            },

            SocketError.AddressAlreadyInUse => new TftpBindException(
                $"UDP/{port} is held exclusively by another process, which on a backup server is the TFTP "
                    + $"server itself. {conflict.Summary}",
                conflict,
                ex)
            {
                Remediation = conflict.Remediation
                    ?? "Stop the TFTP server deliberately before watching, and start it again afterwards.",
            },

            _ => new TftpBindException(
                $"Could not bind 0.0.0.0:{port}: {ex.SocketErrorCode} - {ex.Message}",
                conflict,
                ex),
        };
    }

    private async Task ReceiveLoopAsync(Socket socket, CancellationToken cancellationToken)
    {
        // Sized from the codec's own constant rather than from a guess. A buffer smaller than the
        // largest legal block truncates a big negotiated transfer silently, because a short block
        // is how TFTP says "end of file" - the read succeeds and the file is wrong.
        var buffer = new byte[TftpPacket.MaxDatagramLength];
        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);

        while (!cancellationToken.IsCancellationRequested)
        {
            SocketReceiveMessageFromResult result;
            try
            {
                result = await socket
                    .ReceiveMessageFromAsync(buffer, SocketFlags.None, remote, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex)
            {
                if (IsUnrecoverable(ex.SocketErrorCode))
                {
                    RaiseFault(
                        $"The listening socket failed: {ex.SocketErrorCode} - {ex.Message}. The watch has "
                            + "stopped.",
                        "Restart the watch. If it recurs, check whether the adapter was removed or reset.",
                        ex,
                        isFatal: true);
                    break;
                }

                RaiseFault($"Receive error ({ex.SocketErrorCode}), continuing.", null, ex);
                continue;
            }

            // Deliberately synchronous and deliberately not inlined: decoding is span-based and
            // Span<T> locals are illegal in async methods (CS4013).
            HandleDatagram(socket, buffer, result);
        }
    }

    private void HandleDatagram(Socket socket, byte[] buffer, SocketReceiveMessageFromResult result)
    {
        DateTimeOffset now = _time.GetUtcNow();

        if (result.RemoteEndPoint is not IPEndPoint source)
        {
            return;
        }

        if (!TftpPacket.TryParse(buffer.AsSpan(0, result.ReceivedBytes), out TftpMessage? message, out string? problem)
            || message is null)
        {
            // Unlike UDP/67, this port does not see much unrelated traffic, so something arriving
            // here that is not TFTP is a finding rather than noise worth discarding quietly.
            RaiseFault(
                $"A datagram from {source} on UDP/{_options.ListenPort} could not be read as TFTP: {problem}",
                "If this repeats, something other than a TFTP client is talking to this port.");
            return;
        }

        if (message is not TftpRequestMessage request)
        {
            ReportNonRequest(message, source);
            return;
        }

        int arrivalIndex = result.PacketInformation.Interface;
        NicInfo? nic = _nics.ByIndex(arrivalIndex);
        bool retransmit = _retransmits.IsRetransmit(source, request.FileName);

        if (_options.InterfaceIndexFilter is int wanted && wanted != arrivalIndex)
        {
            // Reported rather than dropped, and deliberately not answered: a refusal is a
            // transmission, and this tool does not transmit onto a segment nobody selected.
            string where = nic is null ? $"interface {arrivalIndex}" : $"[{nic.Index}] {nic.Name}";

            Raise(new TftpRequestEventArgs(
                now,
                request,
                source,
                arrivalIndex,
                nic,
                result.PacketInformation.Address,
                retransmit,
                TftpWatchAction.Ignored,
                $"arrived on {where}, which is not the selected adapter - recorded, and nothing was sent"));
            return;
        }

        if (!_options.SendRefusal)
        {
            Raise(new TftpRequestEventArgs(
                now,
                request,
                source,
                arrivalIndex,
                nic,
                result.PacketInformation.Address,
                retransmit,
                TftpWatchAction.Ignored,
                "recorded; refusals are turned off, so the client will retransmit until it times out"));
            return;
        }

        (TftpWatchAction action, string reason, int? refusedFrom) =
            Refuse(socket, source, result.PacketInformation.Address);

        Raise(new TftpRequestEventArgs(
            now,
            request,
            source,
            arrivalIndex,
            nic,
            result.PacketInformation.Address,
            retransmit,
            action,
            reason)
        {
            RefusalSourcePort = refusedFrom,
        });
    }

    /// <summary>
    /// Sends the refusal. The only thing this class ever transmits.
    ///
    /// <para>By default it goes out of the listening socket, so its source port is 69 rather than a
    /// fresh transfer identifier. RFC 1350 has the server pick a new TID for a transfer, but there
    /// is no transfer here to pick one for, and answering an immediate rejection from the well-known
    /// port is what servers in the field do. <b>Unproven against a FANUC controller</b> - so
    /// <see cref="TftpWatchOptions.RefusalSource"/> makes the other behaviour reachable without a
    /// code change, because the only way to find out is a robot in its boot monitor and nobody gets
    /// two of those.</para>
    ///
    /// <para>Returns the port the refusal actually left from, so the log row can say which of the
    /// two was tried. A refusal that was not sent returns null rather than a port nothing used.</para>
    /// </summary>
    private (TftpWatchAction Action, string Reason, int? FromPort) Refuse(
        Socket listener,
        IPEndPoint source,
        IPAddress arrivalAddress)
    {
        byte[] refusal = TftpPacket.EncodeError(_options.RefusalCode, _options.RefusalMessage);

        try
        {
            if (_options.RefusalSource == TftpRefusalSource.TemporarySocket)
            {
                return RefuseFromTemporarySocket(refusal, source, arrivalAddress);
            }

            listener.SendTo(refusal, SocketFlags.None, source);

            int from = (listener.LocalEndPoint as IPEndPoint)?.Port ?? _options.ListenPort;
            string port = from.ToString(CultureInfo.InvariantCulture);

            return (
                TftpWatchAction.Refused,
                $"recorded, and refused from UDP/{port} so the controller stops rather than "
                    + "retransmitting",
                from);
        }
        catch (SocketException ex)
        {
            RaiseFault(
                $"Recorded the request from {source} but could not send the refusal: {ex.SocketErrorCode} "
                    + $"- {ex.Message}. The controller will retransmit until it times out.",
                "The request itself is in the log, which is the part that matters. If this repeats, check "
                    + "the route back to the controller.",
                ex);

            return (
                TftpWatchAction.SendFailed,
                $"recorded; the refusal could not be sent ({ex.SocketErrorCode})",
                null);
        }
    }

    /// <summary>
    /// The other half of the open question: a refusal carrying a transfer identifier of its own.
    ///
    /// <para>Bound to the address the request was delivered to rather than to <c>0.0.0.0</c>. On a
    /// multi-homed machine the stack may otherwise source the reply from whichever address the
    /// route table prefers, and a correct TFTP client discards an answer from an address it never
    /// wrote to as an unknown transfer id - which reads at the pendant as silence, and would make
    /// this experiment look like a failure of the idea rather than of the binding.</para>
    ///
    /// <para>The socket closes immediately after the send. Anything the controller addresses back
    /// to that port then meets a closed port rather than this tool, which is correct: there is no
    /// transfer here to continue, and the refusal has already said so.</para>
    /// </summary>
    private (TftpWatchAction Action, string Reason, int? FromPort) RefuseFromTemporarySocket(
        byte[] refusal,
        IPEndPoint source,
        IPAddress arrivalAddress)
    {
        IPAddress local = arrivalAddress.Equals(IPAddress.Any) ? IPAddress.Any : arrivalAddress;

        using var temporary = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        temporary.Bind(new IPEndPoint(local, 0));
        temporary.SendTo(refusal, SocketFlags.None, source);

        int from = ((IPEndPoint)temporary.LocalEndPoint!).Port;
        string port = from.ToString(CultureInfo.InvariantCulture);

        return (
            TftpWatchAction.Refused,
            $"recorded, and refused from a fresh transfer identifier (UDP/{port} on {local}) rather "
                + "than from the well-known port",
            from);
    }

    /// <summary>
    /// A DATA, ACK, ERROR or OACK arriving on port 69 rather than on a transfer's own port.
    ///
    /// <para>Worth a sentence rather than a shrug. A transfer moves off the well-known port after
    /// its first packet, so mid-transfer traffic arriving here means one end is addressing the
    /// wrong port - which is what a NAT or a firewall rewriting the ephemeral data port looks like
    /// from this side, and one of the few ways to see that at all without a capture.</para>
    /// </summary>
    private void ReportNonRequest(TftpMessage message, IPEndPoint source)
    {
        if (message is TftpErrorMessage error)
        {
            (string summary, string? remediation) = error.Explain();

            RaiseFault(
                $"{source} sent an error to UDP/{_options.ListenPort} rather than to a transfer's own port: "
                    + summary,
                remediation);
            return;
        }

        RaiseFault(
            $"{source} sent a {message.Describe()} to UDP/{_options.ListenPort}. A transfer moves off the "
                + "well-known port after its first packet, so this belongs on a transfer's own port.",
            "A firewall or NAT rewriting the ephemeral data port produces exactly this. Check for one "
                + "between the controller and the server, and whether it has a TFTP helper enabled.");
    }

    private static bool IsUnrecoverable(SocketError error) => error is
        SocketError.Interrupted or
        SocketError.NotSocket or
        SocketError.OperationAborted or
        SocketError.Shutdown or
        SocketError.SystemNotReady or
        SocketError.NetworkDown;

    private void Raise(TftpRequestEventArgs args) => RequestReceived?.Invoke(this, args);

    private void RaiseFault(string message, string? remediation, Exception? exception = null, bool isFatal = false) =>
        Fault?.Invoke(this, new TftpFaultEventArgs(_time.GetUtcNow(), message, remediation, exception, isFatal));
}
