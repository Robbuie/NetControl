using System.Net;

namespace NetControl.Core.AdapterSetup;

/// <summary>
/// The address to put on the laptop's own adapter. Text-free: built from checked addresses, so a
/// caller holding one has nothing left to parse.
/// </summary>
public sealed record AdapterIpSettings
{
    /// <summary>The laptop's address on the panel network.</summary>
    public required IPAddress Ip { get; init; }

    public required IPAddress Mask { get; init; }

    /// <summary>
    /// Null for none - the right answer for a commissioning cable nearly always. A gateway here becomes
    /// a default route, and a default route on the panel adapter can take the laptop's internet and
    /// VPN traffic away from Wi-Fi.
    /// </summary>
    public IPAddress? Gateway { get; init; }

    /// <summary>
    /// The route metric for <see cref="Gateway"/>. When a gateway is given and this is not, a high
    /// one is used so Wi-Fi's default route still wins - see <see cref="DefaultGatewayMetric"/>.
    /// </summary>
    public int? GatewayMetric { get; init; }

    /// <summary>DNS servers, in order. Empty leaves DNS off this adapter.</summary>
    public IReadOnlyList<IPAddress> Dns { get; init; } = [];

    /// <summary>
    /// High enough that a gateway on the panel adapter never outranks Wi-Fi's or the dock's default
    /// route, which Windows gives automatic metrics well under this.
    /// </summary>
    public const int DefaultGatewayMetric = 500;

    public override string ToString() =>
        $"{Ip} / {Mask}"
        + (Gateway is null ? ", no gateway" : $", gateway {Gateway}")
        + (Dns.Count == 0 ? string.Empty : $", DNS {string.Join(", ", Dns)}");
}
