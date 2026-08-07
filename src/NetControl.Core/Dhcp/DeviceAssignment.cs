using System.Net;
using System.Net.Sockets;

namespace NetControl.Core.Dhcp;

/// <summary>
/// One planned device: the address this MAC is supposed to get. Phase 1 serves nothing that is
/// not on this list — no pools, no leases, no "configure everything you found". Commissioning is
/// not a general-purpose DHCP workload, and a rogue DHCP server on a plant network is a genuinely
/// serious incident.
/// </summary>
public sealed record DeviceAssignment
{
    public DeviceAssignment(
        MacAddress mac,
        IPAddress ip,
        IPAddress subnetMask,
        IPAddress? gateway = null,
        string? hostName = null)
    {
        ArgumentNullException.ThrowIfNull(ip);
        ArgumentNullException.ThrowIfNull(subnetMask);

        if (mac.IsEmpty || mac.IsBroadcast || mac.IsMulticast)
        {
            throw new ArgumentException($"'{mac}' cannot identify a device.", nameof(mac));
        }

        RequireIPv4(ip, nameof(ip));
        RequireIPv4(subnetMask, nameof(subnetMask));

        if (gateway is not null)
        {
            RequireIPv4(gateway, nameof(gateway));
        }

        if (!IsContiguousMask(subnetMask))
        {
            throw new ArgumentException(
                $"{subnetMask} is not a valid subnet mask — the set bits must be contiguous.", nameof(subnetMask));
        }

        Mac = mac;
        Ip = ip;
        SubnetMask = subnetMask;
        Gateway = gateway is null || gateway.Equals(IPAddress.Any) ? null : gateway;
        HostName = string.IsNullOrWhiteSpace(hostName) ? null : hostName.Trim();
    }

    public MacAddress Mac { get; }

    public IPAddress Ip { get; }

    public IPAddress SubnetMask { get; }

    /// <summary>Null when the plan does not set one. Never served as 0.0.0.0.</summary>
    public IPAddress? Gateway { get; }

    public string? HostName { get; }

    /// <summary>
    /// Whether this address lives on the same subnet as an adapter address. Serving an address
    /// the commissioning laptop cannot then reach is nearly always a plan error, and it is much
    /// cheaper to catch here than on site.
    /// </summary>
    public bool IsOnSameSubnetAs(IPAddress? adapterAddress, IPAddress? adapterMask)
    {
        if (adapterAddress is null || adapterMask is null)
        {
            return false;
        }

        if (adapterAddress.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        return SameNetwork(Ip, adapterAddress, adapterMask);
    }

    private static bool SameNetwork(IPAddress a, IPAddress b, IPAddress mask)
    {
        Span<byte> ab = stackalloc byte[4];
        Span<byte> bb = stackalloc byte[4];
        Span<byte> mb = stackalloc byte[4];

        if (!a.TryWriteBytes(ab, out _) || !b.TryWriteBytes(bb, out _) || !mask.TryWriteBytes(mb, out _))
        {
            return false;
        }

        for (int i = 0; i < 4; i++)
        {
            if ((ab[i] & mb[i]) != (bb[i] & mb[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static void RequireIPv4(IPAddress address, string parameterName)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException($"{address} is not an IPv4 address.", parameterName);
        }
    }

    private static bool IsContiguousMask(IPAddress mask)
    {
        Span<byte> bytes = stackalloc byte[4];
        if (!mask.TryWriteBytes(bytes, out int written) || written != 4)
        {
            return false;
        }

        uint value = (uint)((bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3]);

        // A contiguous mask is a run of ones then a run of zeros: inverting it and adding one
        // yields a power of two (or zero, for the all-ones mask).
        uint inverted = ~value;
        return (inverted & (inverted + 1)) == 0;
    }
}
