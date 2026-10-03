using System.Buffers.Binary;
using System.Net;
using System.Text;
using NetControl.Core.Capture;

namespace NetControl.Core.Profinet;

/// <summary>
/// PROFINET DCP frames: identify, get and set, built and read. Big-endian throughout.
///
/// <para>Layout after the EtherType: FrameID (2), ServiceID (1), ServiceType (1), Xid (4),
/// ResponseDelay or reserved (2), DCPDataLength (2), then blocks - Option (1), Suboption (1),
/// DCPBlockLength (2), data, and one pad byte when the length is odd. A response block's data starts
/// with a two-byte BlockInfo; a Set request block's data starts with a two-byte BlockQualifier.</para>
/// </summary>
public static class DcpFrame
{
    public const ushort FrameIdHello = 0xFEFC;
    public const ushort FrameIdGetSet = 0xFEFD;
    public const ushort FrameIdIdentifyRequest = 0xFEFE;
    public const ushort FrameIdIdentifyResponse = 0xFEFF;

    public const byte ServiceGet = 3;
    public const byte ServiceSet = 4;
    public const byte ServiceIdentify = 5;

    public const byte TypeRequest = 0;
    public const byte TypeResponseSuccess = 1;

    public const int HeaderLength = 12;

    /// <summary>The multicast address an Identify All goes to.</summary>
    public static MacAddress IdentifyMulticast { get; } = MacAddress.Parse("01:0E:CF:00:00:00");

    internal const byte OptionIp = 0x01;
    internal const byte OptionDevice = 0x02;
    internal const byte OptionControl = 0x05;
    internal const byte OptionAll = 0xFF;

    internal const byte SubMac = 0x01;
    internal const byte SubIpParameter = 0x02;
    internal const byte SubTypeOfStation = 0x01;
    internal const byte SubNameOfStation = 0x02;
    internal const byte SubDeviceId = 0x03;
    internal const byte SubDeviceRole = 0x04;
    internal const byte SubSignal = 0x03;
    internal const byte SubResponse = 0x04;

    /// <summary>Header fields of a received DCP frame.</summary>
    public readonly record struct Header(ushort FrameId, byte ServiceId, byte ServiceType, uint Xid, int DataLength);

    public static bool TryReadHeader(ReadOnlySpan<byte> payload, out Header header)
    {
        header = default;

        if (payload.Length < HeaderLength)
        {
            return false;
        }

        int length = BinaryPrimitives.ReadUInt16BigEndian(payload[10..]);
        if (payload.Length < HeaderLength + length)
        {
            return false;
        }

        header = new Header(
            BinaryPrimitives.ReadUInt16BigEndian(payload),
            payload[2],
            payload[3],
            BinaryPrimitives.ReadUInt32BigEndian(payload[4..]),
            length);
        return true;
    }

    /// <summary>
    /// Reads an identify response, a get response or a hello into a device. False when the frame is
    /// not one of those or carries no blocks this tool reads.
    /// </summary>
    public static bool TryParseDevice(ReadOnlySpan<byte> payload, MacAddress sender, out DcpDevice? device)
    {
        device = null;

        if (!TryReadHeader(payload, out Header header)
            || header.FrameId is not (FrameIdIdentifyResponse or FrameIdHello or FrameIdGetSet))
        {
            return false;
        }

        string? name = null, type = null, origin = null;
        ushort? vendor = null, deviceId = null;
        byte? role = null;
        IPAddress? address = null, mask = null, gateway = null;
        bool conflict = false, any = false;

        ReadOnlySpan<byte> blocks = payload.Slice(HeaderLength, header.DataLength);
        int at = 0;

        while (at + 4 <= blocks.Length)
        {
            byte option = blocks[at];
            byte sub = blocks[at + 1];
            int length = BinaryPrimitives.ReadUInt16BigEndian(blocks[(at + 2)..]);
            at += 4;

            if (at + length > blocks.Length)
            {
                break;
            }

            ReadOnlySpan<byte> block = blocks.Slice(at, length);
            at += length + (length & 1);

            // Every response block starts with a two-byte BlockInfo.
            if (block.Length < 2)
            {
                continue;
            }

            ushort info = BinaryPrimitives.ReadUInt16BigEndian(block);
            ReadOnlySpan<byte> data = block[2..];

            switch ((option, sub))
            {
                case (OptionDevice, SubNameOfStation):
                    name = Encoding.ASCII.GetString(data).TrimEnd('\0');
                    any = true;
                    break;
                case (OptionDevice, SubTypeOfStation):
                    type = Encoding.ASCII.GetString(data).TrimEnd('\0').Trim();
                    any = true;
                    break;
                case (OptionDevice, SubDeviceId) when data.Length >= 4:
                    vendor = BinaryPrimitives.ReadUInt16BigEndian(data);
                    deviceId = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
                    any = true;
                    break;
                case (OptionDevice, SubDeviceRole) when data.Length >= 1:
                    role = data[0];
                    break;
                case (OptionIp, SubIpParameter) when data.Length >= 12:
                    address = new IPAddress(data[..4]);
                    mask = new IPAddress(data.Slice(4, 4));
                    gateway = new IPAddress(data.Slice(8, 4));
                    origin = (info & 0x03) switch { 1 => "static", 2 => "DHCP", _ => "none" };
                    conflict = (info & 0x80) != 0;
                    any = true;
                    break;
            }
        }

        if (!any)
        {
            return false;
        }

        device = new DcpDevice
        {
            Mac = sender,
            NameOfStation = name ?? string.Empty,
            TypeOfStation = type,
            VendorId = vendor,
            DeviceId = deviceId,
            Role = role,
            Address = address,
            Mask = mask,
            Gateway = gateway,
            AddressOrigin = origin,
            AddressConflict = conflict,
        };

        return true;
    }

    /// <summary>
    /// The device's answer to a Set: the error code of the first Control Response block, or null
    /// when the frame is not a Set response at all.
    /// </summary>
    public static byte? SetResponseError(ReadOnlySpan<byte> payload)
    {
        if (!TryReadHeader(payload, out Header header) || header.FrameId != FrameIdGetSet || header.ServiceId != ServiceSet)
        {
            return null;
        }

        ReadOnlySpan<byte> blocks = payload.Slice(HeaderLength, header.DataLength);
        int at = 0;

        while (at + 4 <= blocks.Length)
        {
            byte option = blocks[at];
            byte sub = blocks[at + 1];
            int length = BinaryPrimitives.ReadUInt16BigEndian(blocks[(at + 2)..]);
            at += 4;

            if (at + length > blocks.Length)
            {
                break;
            }

            if (option == OptionControl && sub == SubResponse && length >= 3)
            {
                return blocks[at + 2];
            }

            at += length + (length & 1);
        }

        // A success response with no Control Response block is still a success.
        return (header.ServiceType & 0x01) != 0 ? (byte)0 : (byte)0xFF;
    }

    /// <summary>Identify All, to the DCP multicast address.</summary>
    internal static byte[] IdentifyAll(MacAddress local, uint xid, ushort responseDelay) =>
        Build(IdentifyMulticast, local, FrameIdIdentifyRequest, ServiceIdentify, xid, responseDelay, [OptionAll, OptionAll, 0x00, 0x00]);

    /// <summary>A Get for the name, address, type, id and role - to one device.</summary>
    internal static byte[] Get(MacAddress target, MacAddress local, uint xid) =>
        Build(target, local, FrameIdGetSet, ServiceGet, xid, 0,
        [
            OptionDevice, SubNameOfStation,
            OptionIp, SubIpParameter,
            OptionDevice, SubTypeOfStation,
            OptionDevice, SubDeviceId,
            OptionDevice, SubDeviceRole,
        ]);

    /// <summary>Set NameOfStation. Qualifier 1 keeps it across a power cycle; 0 does not.</summary>
    internal static byte[] SetName(MacAddress target, MacAddress local, uint xid, string name, bool permanent)
    {
        byte[] text = Encoding.ASCII.GetBytes(name);
        return Build(target, local, FrameIdGetSet, ServiceSet, xid, 0, Block(OptionDevice, SubNameOfStation, Qualifier(permanent), text));
    }

    /// <summary>Set the IP parameters: address, mask, gateway.</summary>
    internal static byte[] SetIp(MacAddress target, MacAddress local, uint xid, IPAddress address, IPAddress mask, IPAddress gateway, bool permanent) =>
        Build(target, local, FrameIdGetSet, ServiceSet, xid, 0,
            Block(OptionIp, SubIpParameter, Qualifier(permanent), [.. address.GetAddressBytes(), .. mask.GetAddressBytes(), .. gateway.GetAddressBytes()]));

    /// <summary>Flash the device's LED so somebody at the panel can see which one it is. Changes nothing stored.</summary>
    internal static byte[] Signal(MacAddress target, MacAddress local, uint xid) =>
        Build(target, local, FrameIdGetSet, ServiceSet, xid, 0, Block(OptionControl, SubSignal, [0x00, 0x00], [0x01, 0x00]));

    private static byte[] Qualifier(bool permanent) => [0x00, permanent ? (byte)0x01 : (byte)0x00];

    private static byte[] Block(byte option, byte sub, byte[] qualifier, byte[] data)
    {
        int length = qualifier.Length + data.Length;
        var block = new byte[4 + length + (length & 1)];
        block[0] = option;
        block[1] = sub;
        BinaryPrimitives.WriteUInt16BigEndian(block.AsSpan(2), (ushort)length);
        qualifier.CopyTo(block, 4);
        data.CopyTo(block, 4 + qualifier.Length);
        return block;
    }

    private static byte[] Build(MacAddress destination, MacAddress source, ushort frameId, byte service, uint xid, ushort delay, byte[] blocks)
    {
        var frame = new byte[EthernetFrame.HeaderLength + HeaderLength + blocks.Length];
        EthernetFrame.Write(frame, destination, source, EthernetFrame.Profinet);

        Span<byte> dcp = frame.AsSpan(EthernetFrame.HeaderLength);
        BinaryPrimitives.WriteUInt16BigEndian(dcp, frameId);
        dcp[2] = service;
        dcp[3] = TypeRequest;
        BinaryPrimitives.WriteUInt32BigEndian(dcp[4..], xid);
        BinaryPrimitives.WriteUInt16BigEndian(dcp[8..], delay);
        BinaryPrimitives.WriteUInt16BigEndian(dcp[10..], (ushort)blocks.Length);
        blocks.CopyTo(dcp[HeaderLength..]);
        return frame;
    }
}
