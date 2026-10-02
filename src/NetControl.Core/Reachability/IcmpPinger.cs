using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NetControl.Core.Reachability;

/// <summary>
/// ICMP echo through <see cref="Ping"/>. On Windows that is the ICMP helper API, which needs no
/// administrator rights and no raw socket - the same reason the tool can run from a USB stick.
///
/// <para>A fresh <see cref="Ping"/> per request. The class refuses a second send while one is in
/// flight, and the sweep has several in flight at once.</para>
/// </summary>
public sealed class IcmpPinger : IPinger
{
    /// <summary>
    /// Thirty-two bytes, the same as Windows' own ping.exe, so a capture of this tool looks like
    /// something an engineer has seen before. The content is irrelevant.
    /// </summary>
    private static readonly byte[] Payload = new byte[32];

    public async Task<PingOutcome> PingAsync(
        IPAddress address, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(address);

        using var ping = new Ping();

        try
        {
            PingReply reply = await ping
                .SendPingAsync(address, timeout, Payload, new PingOptions(ttl: 64, dontFragment: true), cancellationToken)
                .ConfigureAwait(false);

            return Map(address, reply);
        }
        catch (PingException ex)
        {
            // Windows raises this for "no route" as well as for genuine failures, and the inner
            // exception is the one that says which.
            return ex.InnerException is SocketException socket
                && socket.SocketErrorCode is SocketError.NetworkUnreachable or SocketError.HostUnreachable
                ? new PingOutcome { Address = address, Status = PingStatus.Unreachable, Detail = socket.Message }
                : new PingOutcome
                {
                    Address = address,
                    Status = PingStatus.Failed,
                    Detail = ex.InnerException?.Message ?? ex.Message,
                };
        }
    }

    private static PingOutcome Map(IPAddress address, PingReply reply) => reply.Status switch
    {
        IPStatus.Success => new PingOutcome
        {
            Address = address,
            Status = PingStatus.Replied,
            RoundTrip = TimeSpan.FromMilliseconds(reply.RoundtripTime),
            Ttl = reply.Options?.Ttl,
        },

        // Host unreachable is filed as silence, not as "no route". On Windows it is what the local
        // stack reports when an address on this PC's own subnet does not answer ARP - which is to
        // say, nothing is there - and calling that a routing problem would send somebody to look at
        // the laptop's adapter for a device that is simply switched off.
        IPStatus.TimedOut or IPStatus.TimeExceeded or IPStatus.TtlExpired
            or IPStatus.DestinationHostUnreachable => new PingOutcome
            {
                Address = address,
                Status = PingStatus.TimedOut,
                Detail = reply.Status.ToString(),
            },

        IPStatus.DestinationNetworkUnreachable
            or IPStatus.DestinationUnreachable
            or IPStatus.BadRoute => new PingOutcome
            {
                Address = address,
                Status = PingStatus.Unreachable,
                Detail = reply.Status.ToString(),
            },

        _ => new PingOutcome
        {
            Address = address,
            Status = PingStatus.Failed,
            Detail = reply.Status.ToString(),
        },
    };
}
