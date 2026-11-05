using System.Net;
using NetControl.Core.Interfaces;

namespace NetControl.Core.AdapterSetup;

/// <summary>
/// What is wrong with putting an address on the laptop's adapter, before anything is run.
///
/// <para>Two levels, as the plan grid has. <see cref="Problems"/> refuse the change: an address that is
/// not a host on its own subnet, a gateway off it, an address a device in the plan is meant to have,
/// or one another adapter on this laptop already holds. <see cref="Warnings"/> are said and then left
/// to the person: two adapters on one subnet, or a gateway on a commissioning cable, are both
/// sometimes exactly what is meant.</para>
/// </summary>
public sealed record AdapterIpCheck(IReadOnlyList<string> Problems, IReadOnlyList<string> Warnings)
{
    public bool IsOk => Problems.Count == 0;

    /// <param name="mode">The change.</param>
    /// <param name="settings">The address; may be null for <see cref="AdapterIpMode.Dhcp"/>.</param>
    /// <param name="adapter">The adapter as it is now, or null when it cannot be read.</param>
    /// <param name="otherAdapters">Every other adapter on this laptop.</param>
    /// <param name="planned">Every address the open plan gives a device.</param>
    public static AdapterIpCheck Of(
        AdapterIpMode mode,
        AdapterIpSettings? settings,
        AdapterSnapshot? adapter,
        IEnumerable<NicInfo> otherAdapters,
        IEnumerable<IPAddress> planned)
    {
        ArgumentNullException.ThrowIfNull(otherAdapters);
        ArgumentNullException.ThrowIfNull(planned);

        var problems = new List<string>();
        var warnings = new List<string>();

        if (mode == AdapterIpMode.Dhcp)
        {
            warnings.Add("With no DHCP server on the panel network the adapter ends up on a 169.254 address "
                + "and cannot reach any device until it is given one.");
            return new AdapterIpCheck(problems, warnings);
        }

        if (settings is null)
        {
            problems.Add("Type an address and a mask.");
            return new AdapterIpCheck(problems, warnings);
        }

        if (!Ipv4Subnet.TryCreate(settings.Ip, settings.Mask, out Ipv4Subnet subnet))
        {
            problems.Add($"{settings.Mask} is not a subnet mask: the ones have to be contiguous, e.g. 255.255.255.0.");
            return new AdapterIpCheck(problems, warnings);
        }

        if (subnet.PrefixLength < 31 && (settings.Ip.Equals(subnet.Network) || settings.Ip.Equals(subnet.Broadcast)))
        {
            problems.Add($"{settings.Ip} is the {(settings.Ip.Equals(subnet.Network) ? "network" : "broadcast")} address of "
                + $"{subnet.Network}/{subnet.PrefixLength}, not one a host can have.");
        }

        if (settings.Ip.GetAddressBytes() is [0, ..] or [127, ..] or [>= 224, ..] or [169, 254, ..])
        {
            problems.Add($"{settings.Ip} cannot be given to an adapter.");
        }

        if (settings.Gateway is { } gateway)
        {
            if (!subnet.Contains(gateway))
            {
                problems.Add($"The gateway {gateway} is not on {subnet.Network}/{subnet.PrefixLength}, so the adapter "
                    + "could never reach it.");
            }
            else if (gateway.Equals(settings.Ip))
            {
                problems.Add("The gateway cannot be the adapter's own address.");
            }

            warnings.Add("A gateway on the panel adapter adds a default route. It is given a high metric so Wi-Fi "
                + "keeps the internet, but leave it empty unless something off this subnet has to be reached.");
        }

        if (planned.Any(settings.Ip.Equals))
        {
            problems.Add($"{settings.Ip} is planned for a device in the open project. The laptop would answer at "
                + "that device's address.");
        }

        foreach (NicInfo other in otherAdapters)
        {
            if (adapter is not null && other.Index == adapter.Index)
            {
                continue;
            }

            if (other.Owns(settings.Ip))
            {
                problems.Add($"{other.Name} already has {settings.Ip}.");
            }
            else if (other.IsUp && other.PerAddress().Any(nic =>
                Ipv4Subnet.TryCreate(nic.IPv4, nic.Mask, out Ipv4Subnet theirs)
                && theirs.Network.Equals(subnet.Network) && theirs.PrefixLength == subnet.PrefixLength))
            {
                warnings.Add($"{other.Name} is on {subnet.Network}/{subnet.PrefixLength} too. With two adapters on "
                    + "one subnet, Windows picks which one traffic leaves by - unplug or disable the other.");
            }
        }

        if (mode == AdapterIpMode.AddAddress && adapter is not null)
        {
            if (adapter.IsDhcp)
            {
                problems.Add($"{adapter.Name} gets its address from DHCP, and Windows does not allow a second address "
                    + "beside one. Use Replace, or make the adapter static first.");
            }
            else if (adapter.Addresses.Any(a => a.Ip.Equals(settings.Ip)))
            {
                problems.Add($"{adapter.Name} already has {settings.Ip}.");
            }
        }

        return new AdapterIpCheck(problems, warnings);
    }
}
