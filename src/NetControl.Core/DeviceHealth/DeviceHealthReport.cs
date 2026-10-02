using System.Globalization;
using System.Net;
using NetControl.Core.Cip;

namespace NetControl.Core.DeviceHealth;

/// <summary>
/// Everything one diagnostics read got out of one device. Read-only facts; what they mean is
/// <see cref="DeviceHealthAssessment"/>'s job, kept apart so the rules can be tested without a socket.
/// </summary>
public sealed record DeviceHealthReport
{
    /// <summary>The one address that was asked.</summary>
    public required IPAddress Address { get; init; }

    public required DateTimeOffset ReadUtc { get; init; }

    // --- Identity (class 0x01) ---------------------------------------------------------------

    public ushort? VendorId { get; init; }

    public ushort? ProductCode { get; init; }

    public string? Revision { get; init; }

    public uint? SerialNumber { get; init; }

    public string? ProductName { get; init; }

    public IdentityStatus? Status { get; init; }

    // --- TCP/IP Interface (class 0xF5) -------------------------------------------------------

    public TcpIpStatus? TcpIpStatus { get; init; }

    public ConfigMethod? Method { get; init; }

    public InterfaceConfig? Configuration { get; init; }

    public string? HostName { get; init; }

    // --- Ethernet Link (class 0xF6), one per port -------------------------------------------

    public IReadOnlyList<LinkPort> Ports { get; init; } = [];

    /// <summary>
    /// Things the read could not do that are worth saying - an attribute refused, a connection that
    /// dropped part-way. Not findings: they say what is missing, not what is wrong.
    /// </summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    public string SerialText => SerialNumber is { } serial
        ? string.Create(CultureInfo.InvariantCulture, $"{serial:X8}")
        : string.Empty;

    /// <summary>"1756-EN2T/D rev 11.3 serial 00C0FFEE at 192.168.1.51".</summary>
    public string Title =>
        (ProductName ?? "Unidentified device")
        + (Revision is null ? string.Empty : $" rev {Revision}")
        + (SerialNumber is null ? string.Empty : $" serial {SerialText}")
        + $" at {Address}";

    /// <summary>The same device as <paramref name="other"/>, judged by serial and product, never by address.</summary>
    public bool IsSameDeviceAs(DeviceHealthReport? other) =>
        other is not null
        && SerialNumber is not null
        && SerialNumber == other.SerialNumber
        && ProductCode == other.ProductCode
        && VendorId == other.VendorId;
}
