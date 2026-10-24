using System.Net;
using System.Net.Sockets;
using NetControl.Core.Reachability;

namespace NetControl.Core.Commissioning;

/// <summary>
/// One "give this device this address" decision, as typed into the Assign IP dialog: the address,
/// mask and gateway, and whether BOOTP/DHCP should be turned off once the device has taken them.
///
/// <para>This is the Rockwell BOOTP/DHCP tool's workflow in one object - a device asks, you type an
/// address, it is served, and "Disable BOOTP/DHCP" makes it the device's own. Parsing and every rule
/// live here so the dialog can show all the problems at once and the tests can see them.</para>
/// </summary>
public sealed record AddressAssignment
{
    public required IPAddress Ip { get; init; }

    public required IPAddress Mask { get; init; }

    /// <summary>Null for none. Plenty of panel networks have no router at all.</summary>
    public IPAddress? Gateway { get; init; }

    /// <summary>Write it into the device and turn BOOTP/DHCP off once it has taken the address.</summary>
    public bool MakeStatic { get; init; }

    /// <summary>
    /// Validates the three fields against the adapter the device asked on.
    /// </summary>
    /// <param name="adapterSubnets">
    /// Every subnet the selected adapter has an address on - its first address and any extras. Used to
    /// refuse an address this laptop could never reach to turn BOOTP/DHCP off, and to refuse the
    /// laptop's own address.
    /// </param>
    public static bool TryCreate(
        string? ipText,
        string? maskText,
        string? gatewayText,
        bool makeStatic,
        IReadOnlyList<Ipv4Subnet> adapterSubnets,
        out AddressAssignment? assignment,
        out IReadOnlyList<string> problems)
    {
        ArgumentNullException.ThrowIfNull(adapterSubnets);

        assignment = null;
        var found = new List<string>();
        problems = found;

        IPAddress? ip = Parse(ipText, "IP address", required: true, found);
        IPAddress? mask = Parse(maskText, "subnet mask", required: true, found);
        IPAddress? gateway = Parse(gatewayText, "gateway", required: false, found);

        if (mask is not null && !Ipv4Subnet.IsContiguousMask(mask))
        {
            found.Add($"{mask} is not a subnet mask - the ones have to be contiguous, e.g. 255.255.255.0.");
            mask = null;
        }

        if (ip is not null && !UnicastTarget.TryCheck(ip, mask, out string? notOneHost))
        {
            found.Add(notOneHost!);
            ip = null;
        }

        if (ip is not null && adapterSubnets.Any(subnet => subnet.Address.Equals(ip)))
        {
            found.Add($"{ip} is this PC's own address. Give the device a different one.");
            ip = null;
        }

        Ipv4Subnet deviceSubnet = default;
        bool haveSubnet = ip is not null && mask is not null && Ipv4Subnet.TryCreate(ip, mask, out deviceSubnet);

        if (gateway is not null && haveSubnet)
        {
            if (!deviceSubnet.Contains(gateway))
            {
                found.Add($"The gateway {gateway} is not on {deviceSubnet.Network}/{deviceSubnet.PrefixLength}, so the device "
                    + "could never reach it. Leave it blank if there is no router.");
            }
            else if (gateway.Equals(ip))
            {
                found.Add("The gateway cannot be the device's own address.");
            }
        }

        // Turning BOOTP/DHCP off is a TCP connection to the device at its new address. A laptop with
        // no address on that subnet sends it to its own default gateway instead, and the device never
        // hears it - the write fails, and worse, the device has been served an address nobody can
        // finish the job on. Better to say so before anything is sent.
        if (makeStatic && ip is not null && !adapterSubnets.Any(subnet => subnet.Contains(ip)))
        {
            string ours = adapterSubnets.Count == 0
                ? "the selected adapter has no IPv4 address"
                : "the selected adapter is on " + string.Join(", ", adapterSubnets.Select(s => $"{s.Network}/{s.PrefixLength}"));
            found.Add($"This PC cannot reach {ip} to turn BOOTP/DHCP off: {ours}. Give the adapter a second address on "
                + "the device's subnet, or untick \"Disable BOOTP/DHCP\" to serve the address only.");
        }

        if (found.Count > 0 || ip is null || mask is null)
        {
            return false;
        }

        assignment = new AddressAssignment
        {
            Ip = ip,
            Mask = mask,
            Gateway = gateway,
            MakeStatic = makeStatic,
        };
        return true;
    }

    private static IPAddress? Parse(string? text, string what, bool required, List<string> problems)
    {
        string trimmed = text?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {
            if (required)
            {
                problems.Add($"Type the {what}.");
            }

            return null;
        }

        // IPAddress.TryParse accepts "10" and "10.1" as shorthand, which nobody typing into this box
        // ever means. Four dotted numbers or nothing.
        if (trimmed.Split('.').Length != 4
            || !IPAddress.TryParse(trimmed, out IPAddress? parsed)
            || parsed.AddressFamily != AddressFamily.InterNetwork)
        {
            problems.Add($"'{trimmed}' is not an IPv4 {what}. Expected four numbers, e.g. 192.168.1.51.");
            return null;
        }

        return parsed;
    }
}
