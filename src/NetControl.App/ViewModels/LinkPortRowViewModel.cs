using System.Globalization;
using NetControl.Core.DeviceHealth;

namespace NetControl.App.ViewModels;

/// <summary>
/// One port of a device, as the Diagnostics tab lists it: the link line, and the counters worth
/// reading, compressed to one line each. Immutable - a new read builds new rows.
/// </summary>
public sealed class LinkPortRowViewModel
{
    public LinkPortRowViewModel(LinkPort port)
    {
        ArgumentNullException.ThrowIfNull(port);

        Port = port;
    }

    public LinkPort Port { get; }

    public string PortText => Port.Instance.ToString(CultureInfo.InvariantCulture);

    public string LinkText => Port.Summary;

    public string MacText => Port.Mac?.ToString() ?? string.Empty;

    /// <summary>The media counters that explain faults, or why there are none to show.</summary>
    public string MediaText => Port.Media is { } media
        ? string.Create(
            CultureInfo.InvariantCulture,
            $"FCS {media.FcsErrors}, alignment {media.AlignmentErrors}, late collisions {media.LateCollisions}, "
            + $"collisions {media.Collisions}, too long {media.FrameTooLong}, MAC rx/tx {media.MacReceiveErrors}/"
            + $"{media.MacTransmitErrors}")
        : Refusal("media counters");

    public string InterfaceText => Port.Interface is { } counters
        ? string.Create(
            CultureInfo.InvariantCulture,
            $"in {counters.InUnicastPackets} unicast / {counters.InNonUnicastPackets} multicast+broadcast, "
            + $"discards {counters.InDiscards}, errors in/out {counters.InErrors}/{counters.OutErrors}")
        : Refusal("interface counters");

    /// <summary>Red when the port says its own hardware is at fault; amber for half duplex.</summary>
    public bool HasFault => Port.Flags is { LocalHardwareFault: true };

    public bool IsHalfDuplex => Port.Flags is { LinkActive: true, FullDuplex: false };

    private string Refusal(string what) =>
        Port.Refused.FirstOrDefault(r => r.StartsWith(what, StringComparison.Ordinal)) is { } reason
            ? $"not given - {reason}"
            : "not read";
}
