using NetControl.Core.Interfaces;

namespace NetControl.Core.Dhcp;

/// <summary>
/// Decides what, if anything, to answer a request with.
///
/// Phase 1 ships exactly one implementation, <see cref="StaticMapPolicy"/>. This is an interface
/// anyway because a pool-based policy is the obvious later addition, and keeping the decision out
/// of <see cref="DhcpServer"/> is what makes that addable without touching the socket code.
///
/// Implementations must be safe to call from the receive loop and may be mutated concurrently by
/// a UI thread editing the plan.
/// </summary>
public interface IAssignmentPolicy
{
    /// <summary>
    /// Called for every well-formed BOOTREQUEST that passes the interface filter.
    /// </summary>
    /// <param name="request">The parsed request.</param>
    /// <param name="arrivalNic">
    /// The adapter it actually arrived on, as reported by IP_PKTINFO. Null if that adapter
    /// disappeared between arrival and lookup, which a policy should treat as a reason not to
    /// serve rather than something to guess around.
    /// </param>
    AssignmentDecision Decide(BootpPacket request, NicInfo? arrivalNic);
}
