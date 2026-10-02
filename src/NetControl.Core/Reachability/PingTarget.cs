using System.Net;

namespace NetControl.Core.Reachability;

/// <summary>
/// One address to ping, with the mask that goes with it when one is known - which is what lets the
/// sweep refuse a planned directed broadcast as well as the addresses that are never one host.
/// </summary>
public readonly record struct PingTarget(IPAddress Address, IPAddress? Mask = null);
