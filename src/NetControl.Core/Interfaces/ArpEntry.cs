using System.Net;

namespace NetControl.Core.Interfaces;

/// <summary>One row of the machine's ARP cache: an address, the hardware behind it, and how it got there.</summary>
/// <param name="Address">The IPv4 address.</param>
/// <param name="Mac">The hardware address it resolved to.</param>
/// <param name="InterfaceIndex">
/// Which adapter learned it. The same number as <see cref="NicInfo.Index"/>, and worth filtering
/// on: an engineering laptop can hold an entry for 192.168.1.51 on the VPN as well as on the port
/// in your hand, and picking the wrong one puts the wrong MAC into a commissioning plan.
/// </param>
/// <param name="Type">How the entry was learned.</param>
public sealed record ArpEntry(IPAddress Address, MacAddress Mac, int InterfaceIndex, ArpEntryType Type)
{
    /// <summary>
    /// Whether this entry is worth believing. An invalid entry is one Windows has kept but knows
    /// is stale, and a MAC read out of one would be a MAC nothing answers to.
    /// </summary>
    public bool IsUsable => (Type is ArpEntryType.Dynamic or ArpEntryType.Static) && !Mac.IsEmpty;

    public override string ToString() => $"{Address} -> {Mac} on [{InterfaceIndex}] ({Type})";
}
