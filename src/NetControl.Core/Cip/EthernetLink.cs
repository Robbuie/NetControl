using System.Buffers.Binary;
using NetControl.Core.Enip;

namespace NetControl.Core.Cip;

/// <summary>
/// The Ethernet Link object (class 0xF6) on one device: one instance per physical port, and reads
/// only.
///
/// <para><b>There is no write here, and no Get_and_Clear.</b> The counters can be read and zeroed in
/// one service (0x4C), which is tempting for a "since I started looking" figure - but it is a write
/// to somebody's device, and it throws away the history the next person to look will need. Two
/// plain reads and a subtraction give the same figure without touching anything.</para>
///
/// <para>Thin on purpose, the same shape as <see cref="TcpIpInterface"/>: the little-endian decode
/// happens in exactly one place, and the caller reads as a list of questions rather than a run of
/// attribute numbers.</para>
/// </summary>
public sealed class EthernetLink(EnipSession session)
{
    private const ushort AttributeInterfaceSpeed = 1;
    private const ushort AttributeInterfaceFlags = 2;
    private const ushort AttributePhysicalAddress = 3;
    private const ushort AttributeInterfaceCounters = 4;
    private const ushort AttributeMediaCounters = 5;

    private readonly EnipSession _session = session ?? throw new ArgumentNullException(nameof(session));

    /// <summary>
    /// Attribute 1 - the speed the port is running at, <b>in Mb/s</b> (10, 100, 1000), and 0 with no
    /// link. Not bits per second: a device reporting 100,000,000 here is reporting it wrongly.
    /// </summary>
    public async Task<CipReading<uint>> ReadSpeedAsync(ushort instance = 1, CancellationToken cancellationToken = default)
    {
        CipResponse response = await ReadAsync(AttributeInterfaceSpeed, instance, cancellationToken).ConfigureAwait(false);

        return response.IsSuccess
            ? CipReading<uint>.Ok(UInt32(response, "Interface Speed"))
            : CipReading<uint>.Failed(response);
    }

    /// <summary>Attribute 2 - link, duplex, how they were negotiated, and the hardware-fault bit.</summary>
    public async Task<CipReading<LinkFlags>> ReadFlagsAsync(
        ushort instance = 1, CancellationToken cancellationToken = default)
    {
        CipResponse response = await ReadAsync(AttributeInterfaceFlags, instance, cancellationToken).ConfigureAwait(false);

        return response.IsSuccess
            ? CipReading<LinkFlags>.Ok(new LinkFlags(UInt32(response, "Interface Flags")))
            : CipReading<LinkFlags>.Failed(response);
    }

    /// <summary>Attribute 3 - the port's MAC. Six bytes in wire order; this one is not byte-swapped.</summary>
    public async Task<CipReading<MacAddress>> ReadMacAsync(
        ushort instance = 1, CancellationToken cancellationToken = default)
    {
        CipResponse response = await ReadAsync(AttributePhysicalAddress, instance, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccess)
        {
            return CipReading<MacAddress>.Failed(response);
        }

        if (response.Data.Length < MacAddress.Length)
        {
            throw new EnipException($"Physical Address came back as {response.Data.Length} bytes; a MAC is 6.");
        }

        return CipReading<MacAddress>.Ok(new MacAddress(response.Data.AsSpan(0, MacAddress.Length)));
    }

    /// <summary>Attribute 4. Optional in the spec, so a refusal is ordinary.</summary>
    public async Task<CipReading<InterfaceCounters>> ReadInterfaceCountersAsync(
        ushort instance = 1, CancellationToken cancellationToken = default)
    {
        CipResponse response = await ReadAsync(AttributeInterfaceCounters, instance, cancellationToken)
            .ConfigureAwait(false);

        return response.IsSuccess
            ? CipReading<InterfaceCounters>.Ok(InterfaceCounters.Parse(response.Data))
            : CipReading<InterfaceCounters>.Failed(response);
    }

    /// <summary>Attribute 5. Optional in the spec, so a refusal is ordinary.</summary>
    public async Task<CipReading<MediaCounters>> ReadMediaCountersAsync(
        ushort instance = 1, CancellationToken cancellationToken = default)
    {
        CipResponse response = await ReadAsync(AttributeMediaCounters, instance, cancellationToken)
            .ConfigureAwait(false);

        return response.IsSuccess
            ? CipReading<MediaCounters>.Ok(MediaCounters.Parse(response.Data))
            : CipReading<MediaCounters>.Failed(response);
    }

    /// <summary>
    /// True for the statuses a device gives when the port asked about does not exist - which is how
    /// a reader walking instances upward from 1 knows it has run out of ports. Devices disagree on
    /// which of the three to send, so all three count.
    /// </summary>
    public static bool MeansNoSuchInstance(byte status) =>
        status is CipGeneralStatus.ObjectDoesNotExist
            or CipGeneralStatus.PathDestinationUnknown
            or CipGeneralStatus.PathSegmentError;

    private Task<CipResponse> ReadAsync(ushort attribute, ushort instance, CancellationToken cancellationToken) =>
        _session.SendAsync(CipRequest.GetAttribute(CipClass.EthernetLink, attribute, instance), cancellationToken);

    private static uint UInt32(CipResponse response, string what)
    {
        if (response.Data.Length < 4)
        {
            throw new EnipException($"{what} came back as {response.Data.Length} bytes; a UDINT/DWORD is 4.");
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(response.Data);
    }
}
