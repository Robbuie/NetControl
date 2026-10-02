using System.Net;
using System.Net.Sockets;

namespace NetControl.Core.Reachability;

/// <summary>
/// The one rule every probe in this folder - and the CIP diagnostics read - keeps: <b>one host,
/// named by somebody</b>.
///
/// <para>A ping or a TCP connect to a broadcast address is not a probe of a device, it is a probe of
/// every device on the segment at once, and on a plant network full of thin TCP stacks that is how a
/// diagnostic turns into an incident. So the check lives here, once, and everything that transmits
/// to an address it was given calls it before the first packet.</para>
///
/// <para>Loopback is allowed. It is not on anybody's plant network, and it is where every test in
/// this repository points its probes so that <c>dotnet test</c> never puts a packet on a real
/// segment.</para>
/// </summary>
public static class UnicastTarget
{
    /// <summary>
    /// Whether <paramref name="address"/> is one host, and if not, why not in a sentence somebody
    /// can act on.
    /// </summary>
    /// <param name="address">The address somebody typed, selected or planned.</param>
    /// <param name="mask">
    /// The subnet mask that goes with it, when one is known. With it the directed broadcast and the
    /// network address are refused too; without it only the addresses that are never one host are.
    /// </param>
    /// <param name="problem">Null when the address is fine.</param>
    public static bool TryCheck(IPAddress? address, IPAddress? mask, out string? problem)
    {
        problem = null;

        if (address is null)
        {
            problem = "No address was given.";
            return false;
        }

        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            problem = $"'{address}' is not an IPv4 address. Industrial Ethernet on a plant floor is IPv4.";
            return false;
        }

        if (address.Equals(IPAddress.Any))
        {
            problem = "0.0.0.0 is not a host - it is what a device with no address holds.";
            return false;
        }

        if (address.Equals(IPAddress.Broadcast))
        {
            problem = "255.255.255.255 is the broadcast address: every device on the segment would answer.";
            return false;
        }

        byte first = address.GetAddressBytes()[0];
        if (first >= 224)
        {
            problem = first < 240
                ? $"{address} is a multicast group, not one device."
                : $"{address} is in the reserved range above multicast, and is not one device.";
            return false;
        }

        if (mask is not null && Ipv4Subnet.TryCreate(address, mask, out Ipv4Subnet subnet)
            && subnet.PrefixLength < 31)
        {
            // A /31 and a /32 have no broadcast or network address in the usual sense (RFC 3021),
            // so the two checks below would refuse a perfectly good point-to-point host.
            if (address.Equals(subnet.Broadcast))
            {
                problem = $"{address} is the broadcast address of {subnet}: every device on that subnet would "
                    + "answer.";
                return false;
            }

            if (address.Equals(subnet.Network))
            {
                problem = $"{address} is the network address of {subnet}, which no device may hold.";
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// <see cref="TryCheck"/>, throwing. For the paths where a refusal means nothing is sent at all.
    /// </summary>
    public static void Ensure(IPAddress? address, IPAddress? mask = null)
    {
        if (!TryCheck(address, mask, out string? problem))
        {
            throw new ReachabilityException(problem!)
            {
                Remediation = "Probes go to one device at a time. Pick the device from the plan or the scan "
                    + "results, or type its own address.",
            };
        }
    }
}
