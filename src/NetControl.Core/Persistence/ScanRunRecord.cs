using System.Net;

namespace NetControl.Core.Persistence;

/// <summary>
/// One ListIdentity scan: when, from which adapter on which subnet, and how many answered. A
/// <c>ScanRun</c> row. The adapter's address and mask are what decide which earlier scan a later
/// one is compared with - a scan of a different segment is not a "before".
/// </summary>
public sealed record ScanRunRecord(
    long Id,
    DateTimeOffset Utc,
    string NicName,
    IPAddress? NicAddress,
    IPAddress? NicMask,
    int Answered)
{
    /// <summary>The subnet the scan went out on, when the adapter had one.</summary>
    public bool TryGetSubnet(out Ipv4Subnet subnet) => Ipv4Subnet.TryCreate(NicAddress, NicMask, out subnet);
}
