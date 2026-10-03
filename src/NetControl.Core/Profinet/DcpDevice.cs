using System.Globalization;
using System.Net;

namespace NetControl.Core.Profinet;

/// <summary>
/// One PROFINET device as it described itself over DCP: who it is, what it is called on the
/// network, and how it is addressed.
/// </summary>
public sealed record DcpDevice
{
    public required MacAddress Mac { get; init; }

    /// <summary>The NameOfStation - what the controller's configuration finds the device by. Empty when unset.</summary>
    public string NameOfStation { get; init; } = string.Empty;

    /// <summary>TypeOfStation, e.g. "ET200SP" or "S7-1500".</summary>
    public string? TypeOfStation { get; init; }

    public ushort? VendorId { get; init; }

    public ushort? DeviceId { get; init; }

    /// <summary>Device role bits: 1 IO device, 2 IO controller, 4 multidevice, 8 supervisor.</summary>
    public byte? Role { get; init; }

    public IPAddress? Address { get; init; }

    public IPAddress? Mask { get; init; }

    public IPAddress? Gateway { get; init; }

    /// <summary>How the address was set, from the IP block's BlockInfo: "static", "DHCP" or "none".</summary>
    public string? AddressOrigin { get; init; }

    /// <summary>The device's own address conflict detection has fired (BlockInfo bit 7).</summary>
    public bool AddressConflict { get; init; }

    public bool HasName => NameOfStation.Length > 0;

    /// <summary>True when the device reports no address at all - 0.0.0.0, the state a new device ships in.</summary>
    public bool HasNoAddress => Address is null || Address.Equals(IPAddress.Any);

    public string IdText => VendorId is ushort vendor && DeviceId is ushort device
        ? string.Create(CultureInfo.InvariantCulture, $"{vendor:X4}:{device:X4}")
        : string.Empty;

    public string RoleText => Role switch
    {
        null => string.Empty,
        byte r when (r & 0x02) != 0 => "IO controller",
        byte r when (r & 0x01) != 0 => "IO device",
        byte r when (r & 0x08) != 0 => "supervisor",
        _ => "other",
    };

    /// <summary>One line for a list or the record.</summary>
    public string Describe()
    {
        var parts = new List<string>();

        if (TypeOfStation is { Length: > 0 })
        {
            parts.Add(TypeOfStation);
        }

        if (IdText.Length > 0)
        {
            parts.Add($"id {IdText}");
        }

        parts.Add(HasNoAddress ? "no address" : $"{Address}/{Mask}" + (Gateway is null || Gateway.Equals(IPAddress.Any) ? string.Empty : $" gw {Gateway}"));

        if (AddressConflict)
        {
            parts.Add("ADDRESS CONFLICT");
        }

        return string.Join(", ", parts);
    }
}
