using System.Net;

namespace NetControl.Core.Passive;

/// <summary>What one frame said about the device that sent it.</summary>
/// <param name="Mac">The device - the frame's sender, or the client named inside a DHCP request.</param>
/// <param name="Address">The IPv4 address it used or claimed, when the frame carries one.</param>
/// <param name="Source">How it was heard.</param>
/// <param name="Name">A name it gave itself: a host name, a station name, a product name.</param>
/// <param name="Detail">Anything else worth a line - a DHCP vendor class, an LLDP port, an address conflict probe.</param>
/// <param name="Vlan">The 802.1Q tag the frame carried, if any.</param>
public sealed record PassiveSighting(
    MacAddress Mac,
    IPAddress? Address,
    PassiveSource Source,
    string? Name = null,
    string? Detail = null,
    ushort? Vlan = null);
