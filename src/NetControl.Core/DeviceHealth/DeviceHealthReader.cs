using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Text;
using NetControl.Core.Cip;
using NetControl.Core.Enip;
using NetControl.Core.Reachability;

namespace NetControl.Core.DeviceHealth;

/// <summary>
/// Reads everything a device will say about its own health, from one session to one device.
///
/// <para><b>Read-only, by construction.</b> Every request this sends is Get_Attribute_Single. There
/// is no Set, no Reset, no Get_and_Clear and no forward-open anywhere in this file, and the hard rule
/// in CLAUDE.md about never sending one as a side effect of a read is kept by there being nothing
/// here that could.</para>
///
/// <para><b>Each attribute stands alone.</b> A device that refuses its media counters - they are
/// optional - still gets its link speed and duplex reported, and a refusal is a note on the report
/// rather than a failure of the read. Only failing to connect at all throws.</para>
///
/// <para>Ports are found by asking. Ethernet Link instances are read upward from 1 until the device
/// says there is no such instance, up to <see cref="MaxPorts"/> - a module with an embedded switch has
/// two or three, and nothing on a plant floor that speaks CIP has dozens.</para>
/// </summary>
public sealed class DeviceHealthReader(TimeProvider? timeProvider = null)
{
    private const ushort AttributeIdentityVendor = 1;
    private const ushort AttributeIdentityProductCode = 3;
    private const ushort AttributeIdentityRevision = 4;
    private const ushort AttributeIdentityStatus = 5;
    private const ushort AttributeIdentitySerial = 6;
    private const ushort AttributeIdentityProductName = 7;

    private const ushort AttributeTcpIpStatus = 1;
    private const ushort AttributeTcpIpHostName = 6;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>Almost always 44818. Settable so a test can read a simulator on a spare port.</summary>
    public int Port { get; init; } = EnipSession.DefaultPort;

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>The most Ethernet Link instances asked about.</summary>
    public int MaxPorts { get; init; } = 4;

    /// <summary>
    /// Connects to one device and reads its identity, its TCP/IP interface and every port.
    /// </summary>
    /// <param name="address">One device. Checked through <see cref="UnicastTarget"/> before anything is sent.</param>
    /// <param name="mask">The device's planned mask, when known, so a directed broadcast is refused too.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <exception cref="ReachabilityException">The address is not one host. Nothing was sent.</exception>
    /// <exception cref="EnipException">No session could be opened. Nothing was read.</exception>
    public async Task<DeviceHealthReport> ReadAsync(
        IPAddress address, IPAddress? mask = null, CancellationToken cancellationToken = default)
    {
        UnicastTarget.Ensure(address, mask);

        using EnipSession session = await EnipSession
            .ConnectAsync(address, Port, ConnectTimeout, cancellationToken)
            .ConfigureAwait(false);

        var notes = new List<string>();
        var report = new DeviceHealthReport { Address = address, ReadUtc = _time.GetUtcNow() };

        try
        {
            report = await ReadIdentityAsync(session, report, notes, cancellationToken).ConfigureAwait(false);
            report = await ReadTcpIpAsync(session, report, notes, cancellationToken).ConfigureAwait(false);
            report = report with
            {
                Ports = await ReadPortsAsync(session, notes, cancellationToken).ConfigureAwait(false),
            };
        }
        catch (EnipException ex)
        {
            // The session opened and then went away. What was read before that is still true, and
            // throwing it away would leave somebody with less than the device already told us.
            notes.Add($"The connection dropped part-way through the read: {ex.Message}");
        }

        return report with { Notes = notes };
    }

    private static async Task<DeviceHealthReport> ReadIdentityAsync(
        EnipSession session, DeviceHealthReport report, List<string> notes, CancellationToken cancellationToken)
    {
        CipResponse vendor = await Get(session, CipClass.Identity, AttributeIdentityVendor, 1, cancellationToken)
            .ConfigureAwait(false);
        CipResponse product = await Get(session, CipClass.Identity, AttributeIdentityProductCode, 1, cancellationToken)
            .ConfigureAwait(false);
        CipResponse revision = await Get(session, CipClass.Identity, AttributeIdentityRevision, 1, cancellationToken)
            .ConfigureAwait(false);
        CipResponse status = await Get(session, CipClass.Identity, AttributeIdentityStatus, 1, cancellationToken)
            .ConfigureAwait(false);
        CipResponse serial = await Get(session, CipClass.Identity, AttributeIdentitySerial, 1, cancellationToken)
            .ConfigureAwait(false);
        CipResponse name = await Get(session, CipClass.Identity, AttributeIdentityProductName, 1, cancellationToken)
            .ConfigureAwait(false);

        Note(notes, "identity status", status);

        return report with
        {
            VendorId = UInt16(vendor),
            ProductCode = UInt16(product),
            Revision = RevisionText(revision),
            Status = UInt16(status) is { } word ? new IdentityStatus(word) : null,
            SerialNumber = UInt32(serial),
            ProductName = ShortString(name),
        };
    }

    private static async Task<DeviceHealthReport> ReadTcpIpAsync(
        EnipSession session, DeviceHealthReport report, List<string> notes, CancellationToken cancellationToken)
    {
        var tcpIp = new TcpIpInterface(session);

        CipResponse status = await Get(session, CipClass.TcpIpInterface, AttributeTcpIpStatus, 1, cancellationToken)
            .ConfigureAwait(false);
        CipReading<ConfigMethod> method = await tcpIp.ReadConfigMethodAsync(cancellationToken).ConfigureAwait(false);
        CipReading<InterfaceConfig> configuration =
            await tcpIp.ReadConfigurationAsync(cancellationToken).ConfigureAwait(false);
        CipResponse host = await Get(session, CipClass.TcpIpInterface, AttributeTcpIpHostName, 1, cancellationToken)
            .ConfigureAwait(false);

        Note(notes, "TCP/IP status", status);

        if (!configuration.IsSuccess)
        {
            notes.Add($"TCP/IP configuration: {configuration.StatusText}");
        }

        return report with
        {
            TcpIpStatus = UInt32(status) is { } word ? new TcpIpStatus(word) : null,
            Method = method.IsSuccess ? method.Value : null,
            Configuration = configuration.IsSuccess ? configuration.Value : null,
            HostName = CipString(host),
        };
    }

    private async Task<IReadOnlyList<LinkPort>> ReadPortsAsync(
        EnipSession session, List<string> notes, CancellationToken cancellationToken)
    {
        var link = new EthernetLink(session);
        var ports = new List<LinkPort>();

        for (ushort instance = 1; instance <= MaxPorts; instance++)
        {
            CipReading<LinkFlags> flags = await link.ReadFlagsAsync(instance, cancellationToken).ConfigureAwait(false);

            if (!flags.IsSuccess && EthernetLink.MeansNoSuchInstance(flags.Status))
            {
                if (instance == 1)
                {
                    notes.Add($"Ethernet Link: {flags.StatusText}. The device does not publish its link state.");
                }

                break;
            }

            CipReading<uint> speed = await link.ReadSpeedAsync(instance, cancellationToken).ConfigureAwait(false);
            CipReading<MacAddress> mac = await link.ReadMacAsync(instance, cancellationToken).ConfigureAwait(false);
            CipReading<InterfaceCounters> counters =
                await link.ReadInterfaceCountersAsync(instance, cancellationToken).ConfigureAwait(false);
            CipReading<MediaCounters> media =
                await link.ReadMediaCountersAsync(instance, cancellationToken).ConfigureAwait(false);

            var refused = new List<string>();
            Refusal(refused, "interface flags", flags.IsSuccess, flags.StatusText);
            Refusal(refused, "speed", speed.IsSuccess, speed.StatusText);
            Refusal(refused, "interface counters", counters.IsSuccess, counters.StatusText);
            Refusal(refused, "media counters", media.IsSuccess, media.StatusText);

            ports.Add(new LinkPort
            {
                Instance = instance,
                Flags = flags.IsSuccess ? flags.Value : null,
                SpeedMbps = speed.IsSuccess ? speed.Value : null,
                Mac = mac.IsSuccess ? mac.Value : null,
                Interface = counters.IsSuccess ? counters.Value : null,
                Media = media.IsSuccess ? media.Value : null,
                Refused = refused,
            });
        }

        return ports;
    }

    private static Task<CipResponse> Get(
        EnipSession session, ushort cipClass, ushort attribute, ushort instance, CancellationToken cancellationToken) =>
        session.SendAsync(CipRequest.GetAttribute(cipClass, attribute, instance), cancellationToken);

    private static void Note(List<string> notes, string what, CipResponse response)
    {
        if (!response.IsSuccess)
        {
            notes.Add($"{what}: {response.StatusText}");
        }
    }

    private static void Refusal(List<string> refused, string what, bool ok, string statusText)
    {
        if (!ok)
        {
            refused.Add($"{what}: {statusText}");
        }
    }

    private static ushort? UInt16(CipResponse response) =>
        response.IsSuccess && response.Data.Length >= 2
            ? BinaryPrimitives.ReadUInt16LittleEndian(response.Data)
            : null;

    private static uint? UInt32(CipResponse response) =>
        response.IsSuccess && response.Data.Length >= 4
            ? BinaryPrimitives.ReadUInt32LittleEndian(response.Data)
            : null;

    /// <summary>Identity attribute 4: two USINTs, major then minor - formatted as the scan formats it.</summary>
    private static string? RevisionText(CipResponse response) =>
        response.IsSuccess && response.Data.Length >= 2
            ? string.Create(CultureInfo.InvariantCulture, $"{response.Data[0]}.{response.Data[1]:00}")
            : null;

    /// <summary>SHORT_STRING: one length byte, then the characters.</summary>
    private static string? ShortString(CipResponse response)
    {
        if (!response.IsSuccess || response.Data.Length < 1)
        {
            return null;
        }

        int length = Math.Min(response.Data[0], response.Data.Length - 1);
        return Encoding.Latin1.GetString(response.Data, 1, length);
    }

    /// <summary>STRING: a UINT length, then the characters (padded to even, which is ignored).</summary>
    private static string? CipString(CipResponse response)
    {
        if (!response.IsSuccess || response.Data.Length < 2)
        {
            return null;
        }

        int length = Math.Min(BinaryPrimitives.ReadUInt16LittleEndian(response.Data), response.Data.Length - 2);
        return length == 0 ? null : Encoding.Latin1.GetString(response.Data, 2, length);
    }
}
