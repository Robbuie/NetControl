using NetControl.Core.Persistence;

namespace NetControl.Core.Discovery;

/// <summary>Turns what a scan found into the rows the scan history keeps.</summary>
public static class ScanSightings
{
    /// <summary>
    /// One sighting per device that answered, as the device described itself. The address is the one
    /// the device claims to hold, which is the one the plan and the next scan are compared on.
    /// </summary>
    public static IReadOnlyList<ScanSightingRecord> From(DiscoveryResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return [.. result.Devices.Select(device => new ScanSightingRecord
        {
            Address = device.Address,
            Mac = device.Mac,
            VendorId = device.Identity.VendorId,
            DeviceType = device.Identity.DeviceType,
            ProductCode = device.Identity.ProductCode,
            Revision = device.Identity.Revision,
            Serial = device.Identity.SerialNumber,
            ProductName = device.Identity.ProductName,
        })];
    }
}
