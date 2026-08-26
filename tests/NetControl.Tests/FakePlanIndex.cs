using NetControl.App.Serving;
using NetControl.Core;

namespace NetControl.Tests;

/// <summary>A plan the test states outright, so a log test does not have to build a project.</summary>
internal sealed class FakePlanIndex : IPlanIndex
{
    private readonly Dictionary<MacAddress, PlannedDevice> _planned = [];

    /// <summary>Rows that are in the plan but carry no label yet - the normal state after "Add to plan".</summary>
    public FakePlanIndex(params MacAddress[] planned)
    {
        foreach (MacAddress mac in planned)
        {
            _planned[mac] = new PlannedDevice(mac, null);
        }
    }

    public PlannedDevice? Find(MacAddress mac) =>
        _planned.TryGetValue(mac, out PlannedDevice? device) ? device : null;

    /// <summary>Plans a device under a name.</summary>
    public FakePlanIndex Named(MacAddress mac, string name)
    {
        _planned[mac] = new PlannedDevice(mac, name);
        return this;
    }
}
