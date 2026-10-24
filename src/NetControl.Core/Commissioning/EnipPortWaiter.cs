using System.Net;
using System.Net.Sockets;
using NetControl.Core.Reachability;

namespace NetControl.Core.Commissioning;

/// <summary>
/// Waits for a device that has just been handed an address to start answering on TCP/44818.
///
/// <para><b>Why this exists.</b> A device that has just taken a BOOTP or DHCP address is not ready
/// the instant the reply leaves this laptop. It checks the address for a duplicate first (an ARP
/// probe that takes a second or three on most adapters) and only then brings its EtherNet/IP stack
/// up on it. The commissioner's connect has a short timeout on purpose, so going straight from
/// "served" to Set static fails on exactly the devices that are working normally. The Rockwell tool
/// gets away with it because a person is the delay between the two buttons.</para>
///
/// <para>A TCP connect and an immediate close, nothing else: no session is registered and no CIP
/// is sent. Unicast to one address, checked before the first SYN.</para>
/// </summary>
public static class EnipPortWaiter
{
    /// <summary>
    /// True once <paramref name="address"/> accepts a TCP connection on <paramref name="port"/>;
    /// false if it has not by the end of <paramref name="total"/>.
    /// </summary>
    public static async Task<bool> WaitAsync(
        IPAddress address,
        int port,
        TimeSpan total,
        TimeSpan? attemptTimeout = null,
        TimeSpan? pause = null,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        UnicastTarget.Ensure(address);

        TimeProvider time = timeProvider ?? TimeProvider.System;
        TimeSpan perAttempt = attemptTimeout ?? TimeSpan.FromSeconds(2);
        TimeSpan between = pause ?? TimeSpan.FromSeconds(1);
        long started = time.GetTimestamp();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await TryConnectAsync(address, port, perAttempt, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            if (time.GetElapsedTime(started) + between >= total)
            {
                return false;
            }

            await Task.Delay(between, time, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<bool> TryConnectAsync(
        IPAddress address, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var tcp = new TcpClient { NoDelay = true };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        try
        {
            await tcp.ConnectAsync(address, port, deadline.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (SocketException)
        {
            // Refused or unreachable: the device is still coming up, or ARP has not resolved yet.
            return false;
        }
    }
}
