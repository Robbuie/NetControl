using System.Net;

namespace NetControl.Core.Interfaces;

/// <summary>One process already holding a UDP port, as reported by the IP helper API.</summary>
/// <param name="LocalAddress">
/// The address it bound to. <c>0.0.0.0</c> means a wildcard bind, which competes for broadcast
/// traffic on every adapter; anything else competes only on that address's subnet.
/// </param>
public sealed record UdpPortOwner(IPAddress LocalAddress, int Port, int ProcessId, string ProcessName)
{
    public bool IsWildcard => LocalAddress.Equals(IPAddress.Any);

    public override string ToString() => $"{ProcessName} (pid {ProcessId}) on {LocalAddress}:{Port}";
}
