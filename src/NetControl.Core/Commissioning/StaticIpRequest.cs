using System.Net;
using NetControl.Core.Cip;
using NetControl.Core.Enip;

namespace NetControl.Core.Commissioning;

/// <summary>
/// One device, and what it should be told to become.
///
/// <para><see cref="DeviceAddress"/> is where the device is now - typically the address BOOTP just
/// handed it - and the configuration is where it should end up. They are usually the same, because
/// the common job is not moving a device but making the address it already has permanent.</para>
/// </summary>
public sealed record StaticIpRequest
{
    /// <summary>
    /// The one device to talk to. A single address the user selected or typed; never a broadcast,
    /// never a range. This is the safety rule the whole of Phase 2 rests on.
    /// </summary>
    public required IPAddress DeviceAddress { get; init; }

    /// <summary>
    /// The address it should hold. Leave it equal to <see cref="DeviceAddress"/> to pin the device
    /// where it already is, which is the common job.
    /// </summary>
    public required IPAddress Ip { get; init; }

    public required IPAddress Mask { get; init; }

    /// <summary>Null writes 0.0.0.0, which is what a device with no gateway holds anyway.</summary>
    public IPAddress? Gateway { get; init; }

    /// <summary>
    /// What to leave the interface set to. <see cref="ConfigMethod.Static"/> is the whole point -
    /// it is the "disable BOOTP/DHCP" step. Setting it back to BOOTP or DHCP writes only attribute
    /// 3 and leaves the addresses alone, because handing a device back to a plant DHCP server and
    /// then pinning an address into it would be contradictory.
    /// </summary>
    public ConfigMethod Method { get; init; } = ConfigMethod.Static;

    /// <summary>
    /// Whether an Identity Reset may be sent when the device says a configuration does not take
    /// effect without one.
    ///
    /// <para>Off by default, and deliberately a separate decision. Resetting a module is not a
    /// side effect anybody should discover; on a running line it is an outage. Left off, a device
    /// that needs one is reported as <see cref="CommissionOutcome.Unverified"/> with "power-cycle
    /// it and re-check" rather than being quietly restarted.</para>
    /// </summary>
    public bool AllowReset { get; init; }

    /// <summary>Almost always 44818. A parameter so a simulator can be run on a spare port.</summary>
    public int Port { get; init; } = EnipSession.DefaultPort;

    /// <summary>
    /// What this device is already known to do wrong. Currently only
    /// <see cref="DeviceQuirks.RequiresResetToApply"/> changes the sequence; the rest are carried
    /// so the commissioning record says what was known at the time.
    /// </summary>
    public DeviceQuirks Quirks { get; init; }

    /// <summary>True when the device is being moved rather than pinned where it already is.</summary>
    public bool MovesAddress => !Ip.Equals(DeviceAddress);

    /// <summary>Where to look for the device afterwards.</summary>
    public IPAddress VerifyAddress => Method == ConfigMethod.Static ? Ip : DeviceAddress;

    /// <summary>The addresses as attribute 5 wants them.</summary>
    public InterfaceConfig ToInterfaceConfig() => new()
    {
        Ip = Ip,
        Mask = Mask,
        Gateway = Gateway ?? IPAddress.Any,
    };
}
