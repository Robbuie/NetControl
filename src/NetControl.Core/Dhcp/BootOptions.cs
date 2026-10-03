using System.Net;
using System.Text;

namespace NetControl.Core.Dhcp;

/// <summary>
/// What a served device is told beyond its address: where to boot from and what it is called on
/// the network. Per project, the same for every device the project serves.
///
/// <para><b>Both places, the way RFC 2132 intends.</b> A boot file goes in the BOOTP header's
/// <c>file</c> field, which every client reads, and in option 67 for a DHCP client that asked for
/// it. The TFTP server goes in <c>sname</c> and option 66; the next server in <c>siaddr</c>. A DHCP
/// option is added only when the client's parameter request list asks for it - or when the client
/// sent no list at all - because RFC 2131 says to return what was asked for, and a small adapter
/// handed options it did not expect is exactly the kind of thing that drops the whole reply.</para>
///
/// <para>Empty by default, and an empty set changes nothing: the reply is byte for byte what 0.8.0
/// sent.</para>
/// </summary>
public sealed record BootOptions
{
    /// <summary>The header's file field holds 128 octets, one of them the terminating zero.</summary>
    public const int MaxBootFileLength = 127;

    /// <summary>The header's sname field holds 64.</summary>
    public const int MaxServerNameLength = 63;

    /// <summary>One DHCP option carries at most 255 octets.</summary>
    public const int MaxOptionLength = 255;

    public static BootOptions None { get; } = new();

    /// <summary>siaddr: the server the device should load its boot file from. Our own address when unset.</summary>
    public IPAddress? NextServer { get; init; }

    /// <summary>sname and option 66.</summary>
    public string? TftpServerName { get; init; }

    /// <summary>file and option 67.</summary>
    public string? BootFileName { get; init; }

    /// <summary>Option 15.</summary>
    public string? DomainName { get; init; }

    /// <summary>Option 6, in order of preference.</summary>
    public IReadOnlyList<IPAddress> DnsServers { get; init; } = [];

    public bool IsEmpty =>
        NextServer is null && TftpServerName is null && BootFileName is null && DomainName is null
        && DnsServers.Count == 0;

    /// <summary>"next-server 10.0.0.5, boot file 'r30ib.img'", or "none".</summary>
    public string Describe()
    {
        var parts = new List<string>();

        if (NextServer is not null)
        {
            parts.Add($"next-server {NextServer}");
        }

        if (TftpServerName is not null)
        {
            parts.Add($"TFTP server '{TftpServerName}'");
        }

        if (BootFileName is not null)
        {
            parts.Add($"boot file '{BootFileName}'");
        }

        if (DomainName is not null)
        {
            parts.Add($"domain '{DomainName}'");
        }

        if (DnsServers.Count > 0)
        {
            parts.Add($"DNS {string.Join(", ", DnsServers)}");
        }

        return parts.Count == 0 ? "none" : string.Join(", ", parts);
    }

    /// <summary>
    /// Builds a set from what somebody typed, or lists every problem with it at once. Blank fields
    /// are unset fields. Addresses are parsed by <see cref="Plan.PlanValidation.TryParseIPv4"/>,
    /// for the reason that method exists: "10.0.5" must not quietly become 10.0.0.5.
    /// </summary>
    public static bool TryCreate(
        string? nextServer,
        string? tftpServerName,
        string? bootFileName,
        string? domainName,
        string? dnsServers,
        out BootOptions options,
        out IReadOnlyList<string> problems)
    {
        var found = new List<string>();
        IPAddress? next = null;
        var dns = new List<IPAddress>();

        string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

        if (Clean(nextServer) is { } nextText)
        {
            if (!Plan.PlanValidation.TryParseIPv4(nextText, out next) || next is null)
            {
                found.Add($"Next server: '{nextText}' is not an IPv4 address.");
            }
            else if (!Reachability.UnicastTarget.TryCheck(next, null, out string? problem))
            {
                found.Add($"Next server: {problem}");
                next = null;
            }
        }

        string? server = Clean(tftpServerName);
        if (server is not null && !IsPrintableAscii(server, MaxServerNameLength))
        {
            found.Add($"TFTP server name: printable ASCII only, at most {MaxServerNameLength} characters.");
        }

        string? file = Clean(bootFileName);
        if (file is not null && !IsPrintableAscii(file, MaxBootFileLength))
        {
            found.Add($"Boot file: printable ASCII only, at most {MaxBootFileLength} characters - the header's file field is 128 bytes.");
        }

        string? domain = Clean(domainName);
        if (domain is not null && !IsPrintableAscii(domain, MaxOptionLength))
        {
            found.Add($"Domain name: printable ASCII only, at most {MaxOptionLength} characters.");
        }

        if (Clean(dnsServers) is { } dnsText)
        {
            foreach (string part in dnsText.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
            {
                if (Plan.PlanValidation.TryParseIPv4(part, out IPAddress? address) && address is not null)
                {
                    dns.Add(address);
                }
                else
                {
                    found.Add($"DNS servers: '{part}' is not an IPv4 address.");
                }
            }

            if (dns.Count * 4 > MaxOptionLength)
            {
                found.Add("DNS servers: at most 63 fit in one option.");
            }
        }

        problems = found;
        options = found.Count > 0
            ? None
            : new BootOptions
            {
                NextServer = next,
                TftpServerName = server,
                BootFileName = file,
                DomainName = domain,
                DnsServers = dns,
            };

        return found.Count == 0;
    }

    /// <summary>
    /// Writes these options into a reply being built. Header fields always; DHCP options only when
    /// the request asked for them, or asked for nothing in particular.
    /// </summary>
    internal void ApplyTo(BootpPacket reply, BootpPacket request, bool isDhcp)
    {
        if (IsEmpty)
        {
            return;
        }

        if (NextServer is not null)
        {
            reply.ServerIp = NextServer;
        }

        if (TftpServerName is not null)
        {
            reply.ServerHostName = TftpServerName;
        }

        if (BootFileName is not null)
        {
            reply.BootFileName = BootFileName;
        }

        byte[]? requested = request.Options.TryGetValue(DhcpOptionCode.ParameterRequestList, out byte[]? list)
            ? list
            : null;

        // A BOOTP client has no request list; RFC 1497's vendor extensions are all it gets, and 6
        // and 15 are among them. 66 and 67 are DHCP-era: the header fields already carry both.
        bool Wants(byte code, bool dhcpOnly) =>
            (isDhcp || !dhcpOnly) && (requested is null || Array.IndexOf(requested, code) >= 0);

        if (TftpServerName is not null && Wants(DhcpOptionCode.TftpServerName, dhcpOnly: true))
        {
            reply.SetOption(DhcpOptionCode.TftpServerName, Encoding.ASCII.GetBytes(TftpServerName));
        }

        if (BootFileName is not null && Wants(DhcpOptionCode.BootFileName, dhcpOnly: true))
        {
            reply.SetOption(DhcpOptionCode.BootFileName, Encoding.ASCII.GetBytes(BootFileName));
        }

        if (DomainName is not null && Wants(DhcpOptionCode.DomainName, dhcpOnly: false))
        {
            reply.SetOption(DhcpOptionCode.DomainName, Encoding.ASCII.GetBytes(DomainName));
        }

        if (DnsServers.Count > 0 && Wants(DhcpOptionCode.DomainNameServer, dhcpOnly: false))
        {
            reply.SetOption(DhcpOptionCode.DomainNameServer, [.. DnsServers.SelectMany(a => a.GetAddressBytes())]);
        }
    }

    private static bool IsPrintableAscii(string text, int maxLength) =>
        text.Length <= maxLength && text.All(c => c is >= ' ' and <= '~');
}
