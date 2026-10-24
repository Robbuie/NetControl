using NetControl.Core;

namespace NetControl.App.ViewModels;

/// <summary>
/// What the Assign IP dialog opens with: the device, the adapter it asked on, and the fields
/// pre-filled - mask and gateway from the adapter, the address from the plan if the device is
/// already in it, and otherwise blank. The address is a decision off a drawing, never a guess.
/// </summary>
public sealed record AssignDefaults(
    MacAddress Mac,
    string Vendor,
    string AdapterName,
    string AdapterAddress,
    IReadOnlyList<Ipv4Subnet> AdapterSubnets,
    string Ip,
    string Mask,
    string Gateway);
