using System.Net;

namespace NetControl.Core.Reachability;

/// <summary>Every port asked about one address, in the order asked.</summary>
public sealed record ServiceProbeResult(IPAddress Address, IReadOnlyList<ServiceCheck> Checks)
{
    public IReadOnlyList<ServiceCheck> Open => [.. Checks.Where(c => c.IsOpen)];

    /// <summary>The open ports whose protocol a handshake proved.</summary>
    public IReadOnlyList<ServiceCheck> Verified => [.. Checks.Where(c => c.IsVerified)];

    /// <summary>Something at the address answered - with a connection or with a refusal.</summary>
    public bool HostAnswered => Checks.Any(c => c.State is ServiceState.Open or ServiceState.Refused);

    /// <summary>Whether a web page is there to be opened - the next place anybody goes.</summary>
    public bool HasWebPage => Checks.Any(c => c.IsOpen && c.Service.Port is 80 or 443);

    /// <summary>
    /// The URL to open, preferring plain HTTP: on a device with both, the HTTPS one nearly always
    /// carries a self-signed certificate and a browser warning in front of the same page.
    /// </summary>
    public Uri? WebPage =>
        Checks.Any(c => c.IsOpen && c.Service.Port == 80) ? new Uri($"http://{Address}/")
        : Checks.Any(c => c.IsOpen && c.Service.Port == 443) ? new Uri($"https://{Address}/")
        : null;

    /// <summary>The event row's message and the status line.</summary>
    public string Summary
    {
        get
        {
            if (Open.Count > 0)
            {
                string answers = $"{Address} answers on {string.Join(", ", Open.Select(c => c.Service.ToString()))}.";
                IReadOnlyList<ServiceCheck> unproven = [.. Open.Where(c => c.IsUnverified)];

                return unproven.Count == 0
                    ? answers
                    : $"{answers} Not proven: {string.Join(", ", unproven.Select(c => c.Service.ToString()))} - "
                        + "something accepted the connection but did not speak that protocol.";
            }

            return HostAnswered
                ? $"{Address} is up - it refused every port asked about - but offers none of these services."
                : $"Nothing at {Address} answered on any of {Checks.Count} port(s). That is not evidence the device "
                    + "is off: a firewall that drops connections looks exactly the same. Try Ping.";
        }
    }
}
