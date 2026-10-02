using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace NetControl.Core.Reachability;

/// <summary>
/// Asks one device which of a short list of TCP services it answers on.
///
/// <para><b>One connection at a time, closed at once, nothing sent.</b> Industrial devices have thin
/// TCP stacks and some fall over under a port scan, which is why the scanning rule in CLAUDE.md
/// exists. This opens a socket, lets the handshake finish or fail, and closes it - the same thing a
/// browser does before it decides a page is not there - with a pause before the next port. It is
/// eleven ports, not sixty-five thousand.</para>
///
/// <para>One address, checked through <see cref="UnicastTarget"/> before the first connect.</para>
/// </summary>
public sealed class ServiceProbe
{
    /// <summary>
    /// How long a connect may take before the port is "no answer".
    ///
    /// <para>Three seconds, which is longer than it looks like it needs to be. Windows does not give
    /// up on the first reset: it retries the SYN twice more at growing intervals, so a port that is
    /// actively refused takes about two seconds to report as refused. A shorter timeout would file
    /// every closed port as "no answer" - and "refused" is the one that proves the host is up.</para>
    /// </summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Gap between ports.</summary>
    public TimeSpan PauseBetween { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>The ports asked about. <see cref="ServicePort.Industrial"/> unless a caller says otherwise.</summary>
    public IReadOnlyList<ServicePort> Ports { get; init; } = ServicePort.Industrial;

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
            return new ServiceCheck(service, ServiceState.Open, Stopwatch.GetElapsedTime(started));
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
    }
}
