using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetControl.Core.AdapterSetup;

/// <summary>
/// How one adapter was set up at a moment: DHCP or static, every IPv4 address, the gateway and DNS.
///
/// <para>Taken before every change so "put it back" is one click - most often the office network the
/// laptop has to return to at the end of the day. Stored as JSON so it survives the application
/// being closed between the change and the restore.</para>
/// </summary>
public sealed record AdapterSnapshot
{
    /// <summary>The IPv4 interface index, as <see cref="Interfaces.NicInfo.Index"/>.</summary>
    public required int Index { get; init; }

    /// <summary>The adapter's name ("Ethernet 2"). How a stored snapshot is matched to the adapter again.</summary>
    public required string Name { get; init; }

    public bool IsDhcp { get; init; }

    /// <summary>Every IPv4 address with its mask, first one first.</summary>
    public IReadOnlyList<AdapterAddress> Addresses { get; init; } = [];

    public IPAddress? Gateway { get; init; }

    public IReadOnlyList<IPAddress> Dns { get; init; } = [];

    /// <summary>When it was taken.</summary>
    public DateTimeOffset TakenUtc { get; init; }

    /// <summary>"DHCP: 10.1.2.3/255.255.0.0" or "Static: 192.168.1.10/255.255.255.0, 192.168.2.10/...; gateway ...".</summary>
    public string Summary =>
        (IsDhcp ? "DHCP" : "Static")
        + (Addresses.Count == 0 ? ", no address" : ": " + string.Join(", ", Addresses.Select(a => $"{a.Ip}/{a.Mask}")))
        + (Gateway is null ? string.Empty : $"; gateway {Gateway}")
        + (Dns.Count == 0 ? string.Empty : $"; DNS {string.Join(", ", Dns)}");

    /// <summary>
    /// Reads the adapter as Windows has it now. Null when there is no such adapter - unplugged, or the
    /// index has gone. DNS servers and the DHCP flag only come from the OS, so this is the one place
    /// in the feature that does not run on a fake.
    /// </summary>
    public static AdapterSnapshot? Read(int index, TimeProvider? time = null)
    {
        foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                if (!ni.Supports(NetworkInterfaceComponent.IPv4))
                {
                    continue;
                }

                IPInterfaceProperties properties = ni.GetIPProperties();
                IPv4InterfaceProperties? v4 = properties.GetIPv4Properties();

                if (v4 is null || v4.Index != index)
                {
                    continue;
                }

                // Windows-only in the BCL; the tool is Windows-only, and elsewhere "static" is the safe
                // reading - it only decides whether Add is allowed and what Restore puts back.
                bool dhcp = OperatingSystem.IsWindows() && v4.IsDhcpEnabled;

                return new AdapterSnapshot
                {
                    Index = index,
                    Name = ni.Name,
                    IsDhcp = dhcp,
                    Addresses = [.. properties.UnicastAddresses
                        .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                        .Select(a => new AdapterAddress(a.Address, a.IPv4Mask))],
                    Gateway = properties.GatewayAddresses
                        .Select(g => g.Address)
                        .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any)),
                    Dns = [.. properties.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork)],
                    TakenUtc = (time ?? TimeProvider.System).GetUtcNow(),
                };
            }
            catch (NetworkInformationException)
            {
                // Torn down between enumeration and query. The next adapter may still be the one.
            }
        }

        return null;
    }

    /// <summary>The snapshot as JSON, for the file it is kept in.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>A snapshot back from JSON, or null when the text is not one.</summary>
    public static AdapterSnapshot? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AdapterSnapshot>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        Converters = { new IPAddressJsonConverter() },
    };
}

/// <summary>One IPv4 address on an adapter, with its mask.</summary>
public sealed record AdapterAddress(IPAddress Ip, IPAddress Mask);

/// <summary>IPv4 addresses as dotted-quad strings in JSON, rather than as the object they are.</summary>
internal sealed class IPAddressJsonConverter : JsonConverter<IPAddress>
{
    public override IPAddress? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetString() is { } text && IPAddress.TryParse(text, out IPAddress? address) ? address : null;

    public override void Write(Utf8JsonWriter writer, IPAddress value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
