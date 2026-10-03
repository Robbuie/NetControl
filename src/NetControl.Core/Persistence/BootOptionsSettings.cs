using NetControl.Core.Dhcp;

namespace NetControl.Core.Persistence;

/// <summary>
/// Where a project's <see cref="BootOptions"/> live in <see cref="ProjectSettings"/>. Stored as the
/// text somebody typed and parsed back through <see cref="BootOptions.TryCreate"/>, so a value that
/// a later build would refuse is refused on the way in rather than served.
/// </summary>
public static class BootOptionsSettings
{
    public const string NextServerKey = "dhcp.nextServer";
    public const string TftpServerNameKey = "dhcp.tftpServerName";
    public const string BootFileKey = "dhcp.bootFile";
    public const string DomainNameKey = "dhcp.domainName";
    public const string DnsServersKey = "dhcp.dnsServers";

    /// <summary>
    /// The stored options. A stored value that no longer parses - hand-edited, or from a build with
    /// looser rules - is left out and named in <paramref name="problems"/>, never served.
    /// </summary>
    public static BootOptions Load(ProjectSettings settings, out IReadOnlyList<string> problems)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (BootOptions.TryCreate(
                settings.Get(NextServerKey),
                settings.Get(TftpServerNameKey),
                settings.Get(BootFileKey),
                settings.Get(DomainNameKey),
                settings.Get(DnsServersKey),
                out BootOptions options,
                out problems))
        {
            return options;
        }

        return BootOptions.None;
    }

    public static BootOptions Load(ProjectSettings settings) => Load(settings, out _);

    public static void Save(ProjectSettings settings, BootOptions options)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(options);

        settings.Set(NextServerKey, options.NextServer?.ToString());
        settings.Set(TftpServerNameKey, options.TftpServerName);
        settings.Set(BootFileKey, options.BootFileName);
        settings.Set(DomainNameKey, options.DomainName);
        settings.Set(DnsServersKey, options.DnsServers.Count == 0 ? null : string.Join(", ", options.DnsServers));
    }
}
