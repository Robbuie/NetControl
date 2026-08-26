using System.Collections.Frozen;
using NetControl.Core;
using NetControl.Core.Persistence;

namespace NetControl.App.Serving;

/// <summary>
/// <see cref="IPlanIndex"/> over every row in the project, rebuilt whenever the plan changes.
///
/// <para><b>Why this is not read straight off the policy.</b> It used to be, and the policy is the
/// object the receive loop reads, which made it the obviously correct source. But the policy holds
/// only rows that became a valid <see cref="Core.Dhcp.DeviceAssignment"/> - so a device planned
/// with a MAC and no address yet was absent from it, and the log filed that device as a stranger.
/// The two questions turned out to be different questions: the policy answers "may I serve this",
/// this answers "did somebody type this in". Conflating them made the second one wrong.</para>
///
/// <para><b>Why a second copy of the plan is acceptable here.</b> It is rebuilt in the same method
/// that rebuilds the policy, in the same breath, from the same <see cref="DeviceRepository"/>
/// read - see <c>MainViewModel.ReloadPlan</c>. There is exactly one place the two could drift, it
/// is three lines long, and the alternative is a SQLite query per arriving packet.</para>
///
/// <para>The table is swapped wholesale rather than cleared and refilled, so a lookup landing
/// during a reload sees either the old plan or the new one and never a half-built one.</para>
/// </summary>
public sealed class PlanIndex : IPlanIndex
{
    private volatile FrozenDictionary<MacAddress, PlannedDevice> _byMac =
        FrozenDictionary<MacAddress, PlannedDevice>.Empty;

    public int Count => _byMac.Count;

    public PlannedDevice? Find(MacAddress mac) =>
        _byMac.TryGetValue(mac, out PlannedDevice? device) ? device : null;

    /// <summary>Replaces the index with the plan as it now stands.</summary>
    public void Reload(IEnumerable<DeviceRecord> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);

        Dictionary<MacAddress, PlannedDevice> built = [];

        foreach (DeviceRecord device in devices)
        {
            // Last one wins. The file has a UNIQUE constraint on Mac so there is no duplicate to
            // resolve; indexing rather than adding just means a caller passing an ad-hoc sequence
            // cannot make this throw.
            built[device.Mac] = new PlannedDevice(device.Mac, device.DisplayName);
        }

        _byMac = built.ToFrozenDictionary();
    }

    /// <summary>Empties the index. Used when the project is closed or replaced.</summary>
    public void Clear() => _byMac = FrozenDictionary<MacAddress, PlannedDevice>.Empty;
}
