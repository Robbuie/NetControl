using NetControl.Core.Interfaces;

namespace NetControl.Tests;

/// <summary>
/// Presents one adapter as the answer to every interface index.
///
/// A datagram sent over loopback genuinely arrives on the loopback interface, which the real
/// inventory excludes on purpose - no device can ever send a BOOTP request over loopback. A wire
/// test would otherwise hit the "adapter unknown, refusing to guess" path on every packet and
/// never reach the interesting code. Answering any index with a plausible adapter lets the socket
/// path run for real while attribution stays deterministic.
///
/// The adapter is returned unchanged, index and all, so a test can hand over a real adapter and
/// have the reply steered out of it.
/// </summary>
internal sealed class FakeNicInventory(NicInfo nic) : INicInventory
{
    private readonly NicInfo _nic = nic;

    public FakeNicInventory() : this(Nics.Healthy()) { }

    /// <summary>Interface indexes the server asked about, in order. Proof that IP_PKTINFO was consulted.</summary>
    public List<int> Queried { get; } = [];

    public IReadOnlyList<NicInfo> Snapshot() => [_nic];

    public NicInfo? ByIndex(int index)
    {
        Queried.Add(index);
        return _nic;
    }
}
