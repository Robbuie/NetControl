using System.Net;

namespace NetControl.Core.Reachability;

/// <summary>
/// Sends one echo request. An interface so the sweep and everything above it can be tested without
/// ICMP - and without a test ever pinging something that is not loopback.
/// </summary>
public interface IPinger
{
    /// <summary>
    /// One echo request, one answer. Never throws for a host that did not reply; that is a
    /// <see cref="PingStatus"/>, not an exception.
    /// </summary>
    Task<PingOutcome> PingAsync(IPAddress address, TimeSpan timeout, CancellationToken cancellationToken = default);
}
