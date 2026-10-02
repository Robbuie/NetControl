using System.Globalization;
using System.Net;

namespace NetControl.Core.Persistence;

/// <summary>
/// One device that answered one scan, as it described itself. A <c>ScanSighting</c> row.
///
/// <para>Identity is <see cref="VendorId"/> + <see cref="ProductCode"/> + <see cref="Serial"/> - the
/// same key the scan dedupes on - so the same module seen at two addresses is one device that moved,
/// not one that vanished and one that appeared.</para>
/// </summary>
public sealed record ScanSightingRecord
{
    public long Id { get; init; }

    public long ScanRunId { get; init; }

    public required IPAddress Address { get; init; }

    /// <summary>From the ARP table at the time, when it could be resolved. Empty otherwise.</summary>
    public MacAddress Mac { get; init; }

    public ushort VendorId { get; init; }

    public ushort DeviceType { get; init; }

    public ushort ProductCode { get; init; }

    /// <summary>"11.03", as the scan formats it.</summary>
    public string Revision { get; init; } = string.Empty;

    public uint Serial { get; init; }

    public string ProductName { get; init; } = string.Empty;

    public string SerialText => Serial.ToString("X8", CultureInfo.InvariantCulture);

    /// <summary>
    /// What makes two sightings the same device. Serial first; a device that reports serial 0 - they
    /// exist - falls back to its MAC, and one with neither to its address, which is the weakest key
    /// and the only one left.
    /// </summary>
    public string IdentityKey =>
        Serial != 0
            ? string.Create(CultureInfo.InvariantCulture, $"{VendorId}/{ProductCode}/{Serial:X8}")
            : !Mac.IsEmpty
                ? $"mac {Mac}"
                : $"address {Address}";

    /// <summary>"1734-AENT/B serial 00C0FFEE" - what the change list calls a device.</summary>
    public string Name =>
        (ProductName.Length == 0 ? "An unnamed device" : ProductName)
        + (Serial == 0 ? string.Empty : $" serial {SerialText}");
}
