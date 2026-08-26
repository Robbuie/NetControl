using System.Collections.Concurrent;
using NetControl.Core.Interfaces;

namespace NetControl.Core.Dhcp;

/// <summary>
/// Answers only MACs that are explicitly in the plan.
///
/// This is the safety rule the whole product rests on: serve mode must be explicit, and a MAC
/// nobody typed never gets an address. The map is a concurrent dictionary because the UI edits
/// the plan on its own thread while the receive loop reads it.
/// </summary>
public sealed class StaticMapPolicy : IAssignmentPolicy
{
    private readonly ConcurrentDictionary<MacAddress, DeviceAssignment> _map = new();

    public StaticMapPolicy(IEnumerable<DeviceAssignment>? assignments = null)
    {
        if (assignments is null)
        {
            return;
        }

        foreach (DeviceAssignment assignment in assignments)
        {
            Add(assignment);
        }
    }

    /// <summary>
    /// Refuse to serve an address that is not on the arrival adapter's subnet. Almost always a
    /// plan error - the laptop would hand out an address it then cannot talk to - and the whole
    /// point of this tool is to catch that before somebody is standing in front of a panel.
    /// Turn it off only deliberately, for a relayed or multi-subnet setup.
    /// </summary>
    public bool RequireSameSubnet { get; init; } = true;

    public int Count => _map.Count;

    public IReadOnlyCollection<DeviceAssignment> Assignments => _map.Values.ToList();

    public void Add(DeviceAssignment assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        _map[assignment.Mac] = assignment;
    }

    public bool Remove(MacAddress mac) => _map.TryRemove(mac, out _);

    public void Clear() => _map.Clear();

    public bool TryGet(MacAddress mac, out DeviceAssignment? assignment) => _map.TryGetValue(mac, out assignment);

    public AssignmentDecision Decide(BootpPacket request, NicInfo? arrivalNic)
    {
        ArgumentNullException.ThrowIfNull(request);

        MacAddress mac = request.ClientMac;

        if (mac.IsEmpty || mac.IsBroadcast || mac.IsMulticast)
        {
            return AssignmentDecision.Ignore($"client hardware address '{mac}' is not a usable device address");
        }

        if (!_map.TryGetValue(mac, out DeviceAssignment? assignment))
        {
            return AssignmentDecision.Ignore($"{mac} is not in the plan");
        }

        if (arrivalNic is null)
        {
            return AssignmentDecision.Ignore(
                "the adapter this request arrived on is no longer present - refusing to guess which one to reply from");
        }

        if (!arrivalNic.CanServe)
        {
            string why = !arrivalNic.IsUp
                ? $"[{arrivalNic.Index}] {arrivalNic.Name} reports link '{arrivalNic.Status}'"
                : arrivalNic.IPv4 is null
                    ? $"[{arrivalNic.Index}] {arrivalNic.Name} has no IPv4 address to send the reply from"
                    : $"[{arrivalNic.Index}] {arrivalNic.Name} only holds an APIPA address ({arrivalNic.IPv4})";

            return AssignmentDecision.Ignore($"cannot reply: {why}");
        }

        if (RequireSameSubnet && !assignment.IsOnSameSubnetAs(arrivalNic.IPv4, arrivalNic.Mask))
        {
            return AssignmentDecision.Ignore(
                $"planned address {assignment.Ip} is not on the subnet of [{arrivalNic.Index}] {arrivalNic.Name} "
                    + $"({arrivalNic.IPv4}/{arrivalNic.PrefixLength}) - the device would be unreachable from this "
                    + "laptop after it takes the address");
        }

        return AssignmentDecision.Serve(assignment, $"{mac} is planned as {assignment.Ip}");
    }
}
