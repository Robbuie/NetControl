using System.Net;
using NetControl.Core.Enip;

namespace NetControl.Core.Discovery;

/// <summary>
/// Everything one scan produced: the devices, and the account of the scan itself that goes in the
/// append-only event log.
/// </summary>
/// <param name="Report">What went out and what came back, from the scanner.</param>
/// <param name="Devices">One per distinct device, in the order they answered.</param>
public sealed record DiscoveryResult(ScanReport Report, IReadOnlyList<DiscoveredDevice> Devices)
{
    /// <summary>Devices whose MAC could not be resolved, and so cannot become plan rows.</summary>
    public IReadOnlyList<DiscoveredDevice> Unresolved =>
        [.. Devices.Where(d => !d.CanBePlanned)];

    /// <summary>Addresses more than one device answered on. Empty is the expected state.</summary>
    public IReadOnlyList<IPAddress> ContestedAddresses =>
        [.. Devices.Where(d => d.AddressIsContested)
            .Select(d => d.Address)
            .Distinct()];

    /// <summary>
    /// One line for the event log. A scan put packets on a plant network, so it belongs in the
    /// record whether or not it found anything.
    /// </summary>
    public string Summary
    {
        get
        {
            string line = Report.Summary;

            int unresolved = Unresolved.Count;
            if (unresolved > 0)
            {
                line += $" {unresolved} of them could not be matched to a hardware address.";
            }

            IReadOnlyList<IPAddress> contested = ContestedAddresses;
            if (contested.Count > 0)
            {
                line += $" More than one device answered on {string.Join(", ", contested)}.";
            }

            return line;
        }
    }
}
