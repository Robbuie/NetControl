using System.Net;
using System.Net.Sockets;
using System.Text;
using NetControl.Core.Interfaces;

namespace NetControl.Core.Dhcp;

/// <summary>
/// The BOOTP/DHCP engine: one socket, every adapter, per-packet attribution.
///
/// The design decision that matters is the bind. Binding a socket per adapter is the obvious
/// approach and it is what makes tools miss requests, because the user has to have picked the
/// right adapter before anything can arrive. Instead this binds 0.0.0.0:67 once, turns on
/// IP_PKTINFO, and reads <see cref="IPPacketInformation.Interface"/> to learn which adapter each
/// datagram actually came in on. The adapter filter is then applied in software, so a request on
/// the "wrong" adapter is something we can report rather than something we never see.
///
/// The engine has no UI dependency and never writes to a console. It raises events; the WPF app,
/// the CLI and the tests all observe the same ones. Handlers are invoked on the receive loop's
/// thread, so a UI must marshal and no handler should block.
/// </summary>
public sealed class DhcpServer
{
    /// <summary>
    /// IP_UNICAST_IF. Not in <see cref="SocketOptionName"/>, so it is spelled out here.
    /// </summary>
    private const SocketOptionName IpUnicastIf = (SocketOptionName)31;

    /// <summary>
    /// Comfortably larger than the 576-octet maximum DHCP message. Reused for every receive.
    /// </summary>
    private const int ReceiveBufferSize = 2048;

    private readonly INicInventory _nics;
    private readonly IAssignmentPolicy _policy;
    private readonly DhcpServerOptions _options;
    private readonly RetransmitFilter _retransmits;
    private readonly TimeProvider _time;
    private int _running;

    public DhcpServer(
        INicInventory nics,
        IAssignmentPolicy policy,
        DhcpServerOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(nics);
        ArgumentNullException.ThrowIfNull(policy);

        _nics = nics;
        _policy = policy;
        _options = options ?? new DhcpServerOptions();
        _time = timeProvider ?? TimeProvider.System;
        _retransmits = new RetransmitFilter(_options.RetransmitWindow, _time);
    }

    /// <summary>Every well-formed BOOTREQUEST, whether or not it will be answered.</summary>
    public event EventHandler<DhcpRequestEventArgs>? RequestReceived;

    /// <summary>Every reply actually put on the wire. One append-only log row each.</summary>
    public event EventHandler<DhcpReplyEventArgs>? ReplySent;

    /// <summary>Anything the user needs to know about, fatal or not.</summary>
    public event EventHandler<DhcpFaultEventArgs>? Fault;

    /// <summary>Raised once the socket is bound and the loop is about to start receiving.</summary>
    public event EventHandler? Listening;

    public bool IsListening { get; private set; }

    public IPEndPoint? LocalEndPoint { get; private set; }

    public DhcpServerOptions Options => _options;

    /// <summary>
    /// Binds and receives until cancelled. Throws <see cref="DhcpBindException"/> if it cannot
    /// take the port, or if something else holds it in a way that makes delivery ambiguous -
    /// starting anyway and appearing to work is the failure mode this whole product exists to
    /// remove.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            throw new InvalidOperationException("This DhcpServer is already running.");
        }

        try
        {
            PortConflictReport conflict = PortConflictDetector.Inspect(_options.ListenPort);

            if (conflict.Severity == PortConflictSeverity.Serious && _options.RefuseOnSeriousPortConflict)
            {
                throw new DhcpBindException(conflict.Summary, conflict) { Remediation = conflict.Remediation };
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

    private Socket Bind(PortConflictReport conflict)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            // SO_REUSEADDR is needed to coexist with the Hyper-V Default Switch, which binds
            // its own address on port 67 on a stock Windows machine and would otherwise block a
            // wildcard bind outright. The cost is that Windows also lets a second DHCP server
            // sit alongside us with delivery decided arbitrarily - which is exactly why the port
            // conflict check above runs first and refuses rather than shrugging.
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.EnableBroadcast = true;

            // The important line in the whole project: ask the stack to tell us which interface
            // each datagram arrived on, instead of inferring it from the bind address.
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

    private DhcpBindException Explain(SocketException ex, PortConflictReport conflict)
    {
        int port = _options.ListenPort;

        return ex.SocketErrorCode switch
        {
            SocketError.AccessDenied => new DhcpBindException(
                $"Windows refused the bind to 0.0.0.0:{port} (access denied). Low ports are not "
                    + "reserved on Windows, so this is normally a port exclusion range rather than a "
                    + "permissions problem - Hyper-V and WSL both reserve blocks of UDP ports.",
                conflict,
                ex)
            {
                Remediation = "Run: netsh int ipv4 show excludedportrange protocol=udp",
            },

            SocketError.AddressAlreadyInUse => new DhcpBindException(
                $"UDP/{port} is held exclusively by another process. {conflict.Summary}",
                conflict,
                ex)
            {
                Remediation = conflict.Remediation ?? "Stop the process holding the port and try again.",
            },

            _ => new DhcpBindException(
                $"Could not bind 0.0.0.0:{port}: {ex.SocketErrorCode} - {ex.Message}",
                conflict,
                ex),
        };
    }

    private async Task ReceiveLoopAsync(Socket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[ReceiveBufferSize];
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
                        $"The listening socket failed: {ex.SocketErrorCode} - {ex.Message}. The server has stopped.",
                        "Restart the server. If it recurs, check whether the adapter was removed or reset.",
                        ex,
                        isFatal: true);
                    break;
                }

                RaiseFault($"Receive error ({ex.SocketErrorCode}), continuing.", null, ex);
                continue;
            }

            // Deliberately synchronous, and deliberately not inlined into this method: packet
            // decoding is span-based and Span<T> locals are illegal in async methods (CS4013).
            HandleDatagram(socket, buffer, result);
        }
    }

    private void HandleDatagram(Socket socket, byte[] buffer, SocketReceiveMessageFromResult result)
    {
        DateTimeOffset now = _time.GetUtcNow();

        if (!BootpPacket.TryParse(buffer, result.ReceivedBytes, out BootpPacket? packet) || packet is null)
        {
            // Port 67 does see other traffic. Not worth a fault; not worth a log line either.
            return;
        }

        // Ignore BOOTREPLY: that is either our own broadcast looping back or another server on
        // the segment. Answering it would be nonsense; noticing it is Phase 4's job.
        if (!packet.IsRequest)
        {
            return;
        }

        int arrivalIndex = result.PacketInformation.Interface;
        NicInfo? nic = _nics.ByIndex(arrivalIndex);
        bool retransmit = _retransmits.IsRetransmit(packet.TransactionId, packet.ClientMac);

        if (_options.InterfaceIndexFilter is int wanted && wanted != arrivalIndex)
        {
            // Reported rather than dropped. "Your device is asking, but on an adapter you did not
            // select" is the most useful sentence this tool can produce.
            string where = nic is null ? $"interface {arrivalIndex}" : $"[{nic.Index}] {nic.Name}";
            RequestReceived?.Invoke(this, new DhcpRequestEventArgs(
                now,
                packet,
                arrivalIndex,
                nic,
                result.PacketInformation.Address,
                retransmit,
                AssignmentDecision.Ignore($"arrived on {where}, which is not the selected adapter")));
            return;
        }

        AssignmentDecision decision = Decide(packet, nic);

        RequestReceived?.Invoke(this, new DhcpRequestEventArgs(
            now,
            packet,
            arrivalIndex,
            nic,
            result.PacketInformation.Address,
            retransmit,
            decision));

        if (_options.Mode != DhcpServerMode.Serve || !decision.ShouldServe)
        {
            return;
        }

        // decision.ShouldServe means the policy vouched for both the assignment and the adapter,
        // but the send path dereferences both, so verify rather than assume.
        if (decision.Assignment is not { } assignment || nic is null || nic.IPv4 is not { } serverIp)
        {
            return;
        }

        SendReply(socket, packet, assignment, nic, serverIp, now);
    }

    private AssignmentDecision Decide(BootpPacket packet, NicInfo? nic)
    {
        try
        {
            return _policy.Decide(packet, nic);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // A bug in a policy must not take the listener down with it, and it must not quietly
            // turn into a served address either. Refuse, and say why.
            RaiseFault(
                $"The assignment policy threw while deciding about {packet.ClientMac}: {ex.Message}. "
                    + "No reply was sent.",
                "This is a defect. The request is in the log; please report it with the surrounding lines.",
                ex);

            return AssignmentDecision.Ignore("the assignment policy failed");
        }
    }

    private void SendReply(
        Socket socket,
        BootpPacket request,
        DeviceAssignment assignment,
        NicInfo nic,
        IPAddress serverIp,
        DateTimeOffset now)
    {
        uint leaseSeconds = (uint)Math.Clamp(_options.LeaseTime.TotalSeconds, 60, uint.MaxValue);
        BootpPacket? reply = BuildReply(request, assignment, serverIp, leaseSeconds, _options.Boot);
        if (reply is null)
        {
            return;
        }

        // The client has no address yet, so there is nothing to unicast to and no ARP entry to
        // use: the reply is a limited broadcast regardless of what the broadcast flag says. The
        // exception is a relayed request, which goes back to the relay agent that forwarded it.
        IPEndPoint destination = request.GatewayIp.Equals(IPAddress.Any)
            ? new IPEndPoint(IPAddress.Broadcast, _options.ClientPort)
            : new IPEndPoint(request.GatewayIp, _options.ListenPort);

        byte[] bytes = reply.Serialize();

        try
        {
            int sent = Transmit(socket, bytes, destination, nic);

            ReplySent?.Invoke(this, new DhcpReplyEventArgs(
                now,
                request,
                reply,
                reply.MessageType == DhcpMessageType.Nak ? null : assignment,
                nic,
                _options.SendMode,
                destination,
                sent));
        }
        catch (SocketException ex)
        {
            RaiseFault(
                $"Could not send the reply to {request.ClientMac} out [{nic.Index}] {nic.Name}: "
                    + $"{ex.SocketErrorCode} - {ex.Message}",
                _options.SendMode == ReplySendMode.UnicastInterfaceOption
                    ? "Try again with SendMode = PerSocketBind, which binds the adapter's own address instead "
                        + "of steering the broadcast with IP_UNICAST_IF."
                    : $"Check that [{nic.Index}] {nic.Name} still holds {nic.IPv4}.",
                ex);
        }
    }

    private int Transmit(Socket socket, byte[] bytes, IPEndPoint destination, NicInfo nic)
    {
        if (_options.SendMode == ReplySendMode.UnicastInterfaceOption)
        {
            // IP_UNICAST_IF takes the interface index in NETWORK byte order for IPv4. Passing
            // host order fails silently - the packet leaves by whichever adapter Windows would
            // have chosen anyway, which is the bug this option exists to prevent.
            socket.SetSocketOption(SocketOptionLevel.IP, IpUnicastIf, IPAddress.HostToNetworkOrder(nic.Index));
            return socket.SendTo(bytes, destination);
        }

        using var scoped = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        scoped.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        scoped.EnableBroadcast = true;
        scoped.Bind(new IPEndPoint(nic.IPv4!, _options.ListenPort));
        return scoped.SendTo(bytes, destination);
    }

    /// <summary>
    /// Builds the answer for a request, or null when the correct answer is silence.
    ///
    /// Static and side-effect free so it can be unit tested against captured frames without a
    /// socket anywhere near it.
    /// </summary>
    internal static BootpPacket? BuildReply(
        BootpPacket request,
        DeviceAssignment assignment,
        IPAddress serverIp,
        uint leaseSeconds,
        BootOptions? boot = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(assignment);
        ArgumentNullException.ThrowIfNull(serverIp);

        DhcpMessageType? type = request.MessageType;

        // RELEASE and DECLINE are the client telling us something; INFORM asks for configuration
        // without an address. None of the three wants an address handed back.
        if (type is DhcpMessageType.Release or DhcpMessageType.Decline or DhcpMessageType.Inform)
        {
            return null;
        }

        var reply = new BootpPacket
        {
            Op = BootpPacket.OpBootReply,
            HardwareType = request.HardwareType,
            HardwareAddressLength = request.HardwareAddressLength,
            TransactionId = request.TransactionId,
            Flags = request.Flags,
            GatewayIp = request.GatewayIp,
            ClientMac = request.ClientMac,
            ServerIp = serverIp,
            YourIp = assignment.Ip,
        };

        if (type is null)
        {
            // Plain RFC 951 BOOTP. Vendor-extension options for mask and router are fine and
            // useful, but option 53 is not: giving DHCP semantics to a BOOTP client is a good way
            // to get a reply ignored.
            reply.SetOption(DhcpOptionCode.SubnetMask, assignment.SubnetMask);
            AddGateway(reply, assignment);
            boot?.ApplyTo(reply, request, isDhcp: false);
            return reply;
        }

        if (type == DhcpMessageType.Discover)
        {
            reply.SetOption(DhcpOptionCode.DhcpMessageType, DhcpMessageType.Offer);
            AddDhcpCommon(reply, assignment, serverIp, leaseSeconds);
            boot?.ApplyTo(reply, request, isDhcp: true);
            return reply;
        }

        if (type != DhcpMessageType.Request)
        {
            return null;
        }

        // A REQUEST names the address the client believes it is getting, either in option 50 or
        // in ciaddr. If that disagrees with the plan - usually because the device was re-planned
        // since it last booted - the correct answer is NAK, so it restarts and asks again. An ACK
        // carrying a different address leaves the device stuck in a loop nobody can explain.
        IPAddress? wanted = request.GetAddressOption(DhcpOptionCode.RequestedIpAddress);
        if (wanted is null && !request.ClientIp.Equals(IPAddress.Any))
        {
            wanted = request.ClientIp;
        }

        if (wanted is not null && !wanted.Equals(assignment.Ip))
        {
            var nak = new BootpPacket
            {
                Op = BootpPacket.OpBootReply,
                HardwareType = request.HardwareType,
                HardwareAddressLength = request.HardwareAddressLength,
                TransactionId = request.TransactionId,
                Flags = request.Flags,
                GatewayIp = request.GatewayIp,
                ClientMac = request.ClientMac,
            };

            nak.SetOption(DhcpOptionCode.DhcpMessageType, DhcpMessageType.Nak);
            nak.SetOption(DhcpOptionCode.ServerIdentifier, serverIp);
            nak.SetOption(
                DhcpOptionCode.Message,
                Encoding.ASCII.GetBytes($"planned address is {assignment.Ip}"));
            return nak;
        }

        reply.SetOption(DhcpOptionCode.DhcpMessageType, DhcpMessageType.Ack);
        AddDhcpCommon(reply, assignment, serverIp, leaseSeconds);
        boot?.ApplyTo(reply, request, isDhcp: true);
        return reply;
    }

    private static void AddDhcpCommon(
        BootpPacket reply,
        DeviceAssignment assignment,
        IPAddress serverIp,
        uint leaseSeconds)
    {
        reply.SetOption(DhcpOptionCode.ServerIdentifier, serverIp);
        reply.SetOptionUInt32(DhcpOptionCode.IpAddressLeaseTime, leaseSeconds);
        reply.SetOption(DhcpOptionCode.SubnetMask, assignment.SubnetMask);
        AddGateway(reply, assignment);
    }

    private static void AddGateway(BootpPacket reply, DeviceAssignment assignment)
    {
        if (assignment.Gateway is { } gateway)
        {
            reply.SetOption(DhcpOptionCode.Router, gateway);
        }
    }

    private static bool IsUnrecoverable(SocketError error) => error is
        SocketError.Interrupted or
        SocketError.NotSocket or
        SocketError.OperationAborted or
        SocketError.Shutdown or
        SocketError.SystemNotReady or
        SocketError.NetworkDown;

    private void RaiseFault(string message, string? remediation, Exception? exception = null, bool isFatal = false) =>
        Fault?.Invoke(this, new DhcpFaultEventArgs(_time.GetUtcNow(), message, remediation, exception, isFatal));
}
