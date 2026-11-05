using System.Globalization;
using System.Net;

namespace NetControl.Core.AdapterSetup;

/// <summary>
/// The netsh commands that put an address on an adapter, take one off, or put a snapshot back.
///
/// <para><b>Pure text, and that is the point.</b> Nothing here runs anything; the commands are what
/// the window shows before asking, what goes into the record afterwards, and what a test can read.
/// Each line is in netsh's script syntax - no leading "netsh" - so the whole change is one
/// <c>netsh -f</c>, which means one elevation prompt rather than one per line.</para>
///
/// <para><b>By index, never by name.</b> netsh takes either, and adapter names are typed by people:
/// "Ethernet 2", "Panel (USB)", a name with a quote in it. The index is a number.</para>
/// </summary>
public static class NetshScript
{
    /// <summary>The commands for one change.</summary>
    /// <param name="index">The adapter's IPv4 interface index.</param>
    /// <param name="mode">What kind of change.</param>
    /// <param name="settings">The address; ignored, and may be null, for <see cref="AdapterIpMode.Dhcp"/>.</param>
    public static IReadOnlyList<string> For(int index, AdapterIpMode mode, AdapterIpSettings? settings)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(index);

        string name = index.ToString(CultureInfo.InvariantCulture);

        if (mode == AdapterIpMode.Dhcp)
        {
            return
            [
                $"interface ipv4 set address name={name} source=dhcp",
                $"interface ipv4 set dnsservers name={name} source=dhcp",
            ];
        }

        ArgumentNullException.ThrowIfNull(settings);

        var lines = new List<string>();
        string gateway = Gateway(settings);

        if (mode == AdapterIpMode.Replace)
        {
            lines.Add($"interface ipv4 set address name={name} source=static address={settings.Ip} mask={settings.Mask} {gateway}");
            lines.AddRange(DnsLines(name, settings.Dns));
        }
        else
        {
            // Add leaves DNS alone: the adapter already has whatever its first address came with.
            lines.Add($"interface ipv4 add address name={name} address={settings.Ip} mask={settings.Mask}"
                + (settings.Gateway is null ? string.Empty : $" {gateway}"));
        }

        return lines;
    }

    /// <summary>
    /// The commands that put an adapter back the way a snapshot found it: DHCP, or every static
    /// address in order with the gateway and DNS it had.
    /// </summary>
    public static IReadOnlyList<string> Restore(AdapterSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.IsDhcp || snapshot.Addresses.Count == 0)
        {
            return For(snapshot.Index, AdapterIpMode.Dhcp, null);
        }

        string name = snapshot.Index.ToString(CultureInfo.InvariantCulture);
        AdapterAddress first = snapshot.Addresses[0];

        var lines = new List<string>
        {
            $"interface ipv4 set address name={name} source=static address={first.Ip} mask={first.Mask} "
                + (snapshot.Gateway is null ? "gateway=none" : $"gateway={snapshot.Gateway}"),
        };

        foreach (AdapterAddress other in snapshot.Addresses.Skip(1))
        {
            lines.Add($"interface ipv4 add address name={name} address={other.Ip} mask={other.Mask}");
        }

        lines.AddRange(DnsLines(name, snapshot.Dns));
        return lines;
    }

    /// <summary>Lines as somebody would type them at a prompt - "netsh " in front - for showing and recording.</summary>
    public static string Display(IEnumerable<string> lines) =>
        string.Join(Environment.NewLine, lines.Select(line => "netsh " + line));

    private static string Gateway(AdapterIpSettings settings) =>
        settings.Gateway is null
            ? "gateway=none"
            : $"gateway={settings.Gateway} gwmetric={(settings.GatewayMetric ?? AdapterIpSettings.DefaultGatewayMetric).ToString(CultureInfo.InvariantCulture)}";

    private static IEnumerable<string> DnsLines(string name, IReadOnlyList<IPAddress> dns)
    {
        if (dns.Count == 0)
        {
            yield return $"interface ipv4 set dnsservers name={name} source=static address=none register=none validate=no";
            yield break;
        }

        yield return $"interface ipv4 set dnsservers name={name} source=static address={dns[0]} register=none validate=no";

        for (int i = 1; i < dns.Count; i++)
        {
            yield return $"interface ipv4 add dnsservers name={name} address={dns[i]} index={(i + 1).ToString(CultureInfo.InvariantCulture)} validate=no";
        }
    }
}
