using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace NetControl.Core.Reachability;

/// <summary>
/// Asks one device which of a short list of TCP services it answers on, and - where a port is
/// open and a handshake is known for it - whether the protocol behind it is really the one the port
/// is named for.
///
/// <para><b>One connection at a time.</b> Industrial devices have thin TCP stacks and some fall over
/// under a port scan, which is why the scanning rule in CLAUDE.md exists. This opens a socket, and
/// closes it once it has its answer, with a pause before the next port. It is a dozen ports, not
/// sixty-five thousand.</para>
///
/// <para><b>What is sent on an open port is a read and nothing else.</b> See
/// <see cref="IProtocolHandshake"/>: an identity request, a model name, a hello, a HEAD. With
/// <see cref="VerifyProtocols"/> off, nothing at all is sent - the 0.8.0 behaviour.</para>
///
/// <para>One address, checked through <see cref="UnicastTarget"/> before the first connect.</para>
/// </summary>
public sealed class ServiceProbe
{
    /// <summary>The largest reply read from any port. Every handshake's answer fits well inside it.</summary>
    private const int MaxReply = 4096;

    /// <summary>
    /// How long a connect may take before the port is "no answer".
    ///
    /// <para>Three seconds, which is longer than it looks like it needs to be. Windows does not give
    /// up on the first reset: it retries the SYN twice more at growing intervals, so a port that is
    /// actively refused takes about two seconds to report as refused. A shorter timeout would file
    /// every closed port as "no answer" - and "refused" is the one that proves the host is up.</para>
    /// </summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>How long an open port has to answer its handshake.</summary>
    public TimeSpan ReplyTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Gap between ports.</summary>
    public TimeSpan PauseBetween { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>The ports asked about. <see cref="ServicePort.Industrial"/> unless a caller says otherwise.</summary>
    public IReadOnlyList<ServicePort> Ports { get; init; } = ServicePort.Industrial;

    /// <summary>Whether an open port is asked to prove its protocol. On by default.</summary>
    public bool VerifyProtocols { get; init; } = true;

    /// <summary>Which handshake goes with which port.</summary>
    public IReadOnlyDictionary<int, IProtocolHandshake> Handshakes { get; init; } = ProtocolHandshakes.ByPort;

    /// <summary>Raised after each port, so a long list can show progress.</summary>
    public event EventHandler<ServiceCheck>? Checked;

    /// <exception cref="ReachabilityException">The address is not one host. Nothing was sent.</exception>
    public async Task<ServiceProbeResult> RunAsync(
        IPAddress address, IPAddress? mask = null, CancellationToken cancellationToken = default)
    {
        UnicastTarget.Ensure(address, mask);

        var checks = new List<ServiceCheck>(Ports.Count);

        for (int i = 0; i < Ports.Count; i++)
        {
            if (i > 0 && PauseBetween > TimeSpan.Zero)
            {
                await Task.Delay(PauseBetween, cancellationToken).ConfigureAwait(false);
            }

            ServiceCheck check = await CheckAsync(address, Ports[i], cancellationToken).ConfigureAwait(false);
            checks.Add(check);
            Checked?.Invoke(this, check);
        }

        return new ServiceProbeResult(address, checks);
    }

    private async Task<ServiceCheck> CheckAsync(IPAddress address, ServicePort service, CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ConnectTimeout);

        using var client = new TcpClient(AddressFamily.InterNetwork);

        try
        {
            await client.ConnectAsync(address, service.Port, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ServiceCheck(service, ServiceState.NoAnswer, Stopwatch.GetElapsedTime(started));
        }
        catch (SocketException ex)
        {
            ServiceState state = ex.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => ServiceState.Refused,
                SocketError.TimedOut or SocketError.HostUnreachable or SocketError.NetworkUnreachable
                    => ServiceState.NoAnswer,
                _ => ServiceState.Failed,
            };

            return new ServiceCheck(service, state, Stopwatch.GetElapsedTime(started), ex.SocketErrorCode.ToString());
        }

        TimeSpan connected = Stopwatch.GetElapsedTime(started);

        if (!VerifyProtocols || !Handshakes.TryGetValue(service.Port, out IProtocolHandshake? handshake))
        {
            return new ServiceCheck(service, ServiceState.Open, connected);
        }

        ProtocolVerdict verdict = await HandshakeAsync(client, handshake, address, service.Port, cancellationToken)
            .ConfigureAwait(false);

        return new ServiceCheck(service, ServiceState.Open, connected, Protocol: verdict);
    }

    /// <summary>
    /// Sends the handshake's request and reads until the reply is complete, the far end closes, or
    /// the reply timeout runs out - then judges whatever arrived. A device that resets the
    /// connection when spoken to is a verdict, not an error: it is something worth knowing about
    /// what is on that port.
    /// </summary>
    private async Task<ProtocolVerdict> HandshakeAsync(
        TcpClient client, IProtocolHandshake handshake, IPAddress address, int port, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ReplyTimeout);

        var buffer = new byte[MaxReply];
        int received = 0;

        try
        {
            NetworkStream stream = client.GetStream();
            byte[] request = handshake.BuildRequest(address, port);

            if (request.Length > 0)
            {
                await stream.WriteAsync(request, deadline.Token).ConfigureAwait(false);
            }

            while (received < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(received), deadline.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                received += read;

                if (IsComplete(handshake, buffer, received))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (received == 0)
            {
                return ProtocolVerdict.Silent(handshake.Protocol, ReplyTimeout);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            if (received == 0)
            {
                return ProtocolVerdict.Other(handshake.Protocol, "closed the connection when spoken to");
            }
        }

        return received == 0
            ? ProtocolVerdict.Other(handshake.Protocol, "closed the connection without answering")
            : handshake.Judge(buffer.AsSpan(0, received), address);
    }

    /// <summary>Out of the async method because it takes a span - see CLAUDE.md on CS4013.</summary>
    private static bool IsComplete(IProtocolHandshake handshake, byte[] buffer, int received) =>
        handshake.ExpectedLength(buffer.AsSpan(0, received)) is int wanted && received >= wanted;
}
