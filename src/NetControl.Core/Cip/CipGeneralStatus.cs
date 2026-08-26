using System.Globalization;

namespace NetControl.Core.Cip;

/// <summary>
/// CIP general status codes, and what they mean to somebody standing in front of a panel.
///
/// <para>The wording is the product here. "General status 0x0E" sends a controls engineer to a
/// PDF; "the device is refusing the write - its address is usually pinned by switches on the
/// module" sends them to the module. Every string names the likely cause.</para>
/// </summary>
public static class CipGeneralStatus
{
    public const byte Success = 0x00;

    public const byte PathSegmentError = 0x04;

    public const byte PathDestinationUnknown = 0x05;

    public const byte ServiceNotSupported = 0x08;

    public const byte InvalidAttributeValue = 0x09;

    /// <summary>
    /// What a device sends when it is asked to write the interface configuration while still in
    /// BOOTP or DHCP mode. Seeing this means the write order was wrong.
    /// </summary>
    public const byte ObjectStateConflict = 0x0C;

    public const byte AttributeNotSettable = 0x0E;

    public const byte PrivilegeViolation = 0x0F;

    public const byte DeviceStateConflict = 0x10;

    public const byte NotEnoughData = 0x13;

    public const byte AttributeNotSupported = 0x14;

    public const byte TooMuchData = 0x15;

    public static string Describe(byte status, IReadOnlyList<ushort>? additionalStatus = null)
    {
        string text = status switch
        {
            Success => "Success",
            0x02 => "Resource unavailable",
            PathSegmentError => "Path segment error - the device does not have that class or instance",
            PathDestinationUnknown => "Path destination unknown",
            ServiceNotSupported => "Service not supported by this object",
            InvalidAttributeValue => "Invalid attribute value - the device rejected the value as out of range",
            0x0B => "Already in the requested mode or state",
            ObjectStateConflict =>
                "Object state conflict - usually means the interface is still in BOOTP or DHCP mode",
            AttributeNotSettable =>
                "Attribute not settable - the device is refusing the write, commonly because the address is "
                + "pinned by switches on the module",
            PrivilegeViolation => "Privilege violation - the device requires authentication or is write-protected",
            DeviceStateConflict => "Device state conflict - the device must be idle before it will accept this",
            NotEnoughData => "Not enough data - the request was shorter than the attribute needs",
            AttributeNotSupported => "Attribute not supported by this object",
            TooMuchData => "Too much data - the request was longer than the attribute takes",
            0x1E => "Embedded service error",
            _ => $"General status 0x{status.ToString("X2", CultureInfo.InvariantCulture)}",
        };

        if (additionalStatus is not { Count: > 0 })
        {
            return text;
        }

        IEnumerable<string> extra = additionalStatus
            .Select(value => $"0x{value.ToString("X4", CultureInfo.InvariantCulture)}");

        return $"{text} [additional: {string.Join(", ", extra)}]";
    }
}
