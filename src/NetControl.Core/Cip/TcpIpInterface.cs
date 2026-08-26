using System.Buffers.Binary;
using NetControl.Core.Enip;

namespace NetControl.Core.Cip;

/// <summary>
/// The TCP/IP Interface Object (class 0xF5) on one device, as four typed operations.
///
/// <para>Thin on purpose. It exists so the commissioner reads as the sequence it is - capability,
/// control, configuration, read back - rather than as a run of attribute numbers and byte arrays,
/// and so that the little-endian decode of a UDINT happens in exactly one place.</para>
/// </summary>
public sealed class TcpIpInterface(EnipSession session)
{
    private const ushort AttributeConfigurationCapability = 2;
    private const ushort AttributeConfigurationControl = 3;
    private const ushort AttributeInterfaceConfiguration = 5;

    /// <summary>Attribute 3 carries the method in its low nibble; the rest is reserved.</summary>
    private const uint ConfigMethodMask = 0x0F;

    private readonly EnipSession _session = session ?? throw new ArgumentNullException(nameof(session));

    /// <summary>
    /// Attribute 2 - what the device will let you do to its address. Read this first, every time.
    /// </summary>
    public async Task<CipReading<ConfigCapability>> ReadCapabilityAsync(CancellationToken cancellationToken = default)
    {
        CipResponse response = await _session
            .SendAsync(CipRequest.GetAttribute(CipClass.TcpIpInterface, AttributeConfigurationCapability),
                cancellationToken)
            .ConfigureAwait(false);

        return response.IsSuccess
            ? CipReading<ConfigCapability>.Ok((ConfigCapability)ReadUInt32(response, "Configuration Capability"))
            : CipReading<ConfigCapability>.Failed(response);
    }

    /// <summary>Attribute 3 - Static, BOOTP or DHCP.</summary>
    public async Task<CipReading<ConfigMethod>> ReadConfigMethodAsync(CancellationToken cancellationToken = default)
    {
        CipResponse response = await _session
            .SendAsync(CipRequest.GetAttribute(CipClass.TcpIpInterface, AttributeConfigurationControl),
                cancellationToken)
            .ConfigureAwait(false);

        return response.IsSuccess
            ? CipReading<ConfigMethod>.Ok(
                (ConfigMethod)(ReadUInt32(response, "Configuration Control") & ConfigMethodMask))
            : CipReading<ConfigMethod>.Failed(response);
    }

    /// <summary>Attribute 5 - the addresses as the device currently holds them.</summary>
    public async Task<CipReading<InterfaceConfig>> ReadConfigurationAsync(
        CancellationToken cancellationToken = default)
    {
        CipResponse response = await _session
            .SendAsync(CipRequest.GetAttribute(CipClass.TcpIpInterface, AttributeInterfaceConfiguration),
                cancellationToken)
            .ConfigureAwait(false);

        return response.IsSuccess
            ? CipReading<InterfaceConfig>.Ok(InterfaceConfig.Parse(response.Data))
            : CipReading<InterfaceConfig>.Failed(response);
    }

    /// <summary>
    /// Attribute 3 - this is the write that turns BOOTP/DHCP off (or back on).
    ///
    /// <para>It goes before the address write, always. Several adapters refuse attribute 5 while
    /// still in a dynamic mode, and answer with <see cref="CipGeneralStatus.ObjectStateConflict"/>
    /// - a status that gives no hint at all that the order was the problem.</para>
    /// </summary>
    public Task<CipResponse> WriteConfigMethodAsync(
        ConfigMethod method, CancellationToken cancellationToken = default)
    {
        var data = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(data, (uint)method);

        return _session.SendAsync(
            CipRequest.SetAttribute(CipClass.TcpIpInterface, AttributeConfigurationControl, data),
            cancellationToken);
    }

    /// <summary>Attribute 5 - the addresses.</summary>
    public Task<CipResponse> WriteConfigurationAsync(
        InterfaceConfig configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return _session.SendAsync(
            CipRequest.SetAttribute(CipClass.TcpIpInterface, AttributeInterfaceConfiguration,
                configuration.Serialize()),
            cancellationToken);
    }

    private static uint ReadUInt32(CipResponse response, string what)
    {
        if (response.Data.Length < 4)
        {
            throw new EnipException($"{what} came back as {response.Data.Length} bytes; a UDINT is 4.");
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(response.Data);
    }
}
