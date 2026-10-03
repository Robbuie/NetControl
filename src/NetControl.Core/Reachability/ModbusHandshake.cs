using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace NetControl.Core.Reachability;

/// <summary>
/// Modbus function 43 / MEI 14, Read Device Identification, basic objects (vendor, product code,
/// revision). A read; there is no register in it and nothing to write.
///
/// <para><b>An exception reply still confirms Modbus.</b> Plenty of drives and meters do not
/// implement this function and answer "illegal function" - which only a Modbus server can say, in a
/// frame only Modbus uses. Unit 255 is the one the spec reserves for "the device at this address
/// rather than one behind it", so a gateway is not asked to forward the question onto its serial
/// line.</para>
/// </summary>
public sealed class ModbusHandshake : IProtocolHandshake
{
    internal const ushort TransactionId = 0x4E43;   // "NC"
    private const byte UnitId = 0xFF;
    private const byte ReadDeviceId = 0x2B;
    private const byte MeiDeviceId = 0x0E;
    private const int MbapLength = 7;

    public string Protocol => "Modbus/TCP";

    public byte[] BuildRequest(IPAddress address, int port)
    {
        var frame = new byte[MbapLength + 4];
        BinaryPrimitives.WriteUInt16BigEndian(frame, TransactionId);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), 0);   // protocol id: Modbus
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), 5);   // unit + 4 PDU bytes
        frame[6] = UnitId;
        frame[7] = ReadDeviceId;
        frame[8] = MeiDeviceId;
        frame[9] = 0x01;   // basic identification
        frame[10] = 0x00;  // starting at object 0
        return frame;
    }

    public int? ExpectedLength(ReadOnlySpan<byte> received) =>
        received.Length < 6 ? null : 6 + BinaryPrimitives.ReadUInt16BigEndian(received[4..]);

    public ProtocolVerdict Judge(ReadOnlySpan<byte> reply, IPAddress address)
    {
        if (reply.Length < MbapLength + 2
            || BinaryPrimitives.ReadUInt16BigEndian(reply) != TransactionId
            || BinaryPrimitives.ReadUInt16BigEndian(reply[2..]) != 0)
        {
            return ProtocolVerdict.Other(Protocol, "answered, but not with a Modbus/TCP header");
        }

        byte function = reply[7];

        if (function == (ReadDeviceId | 0x80))
        {
            byte code = reply[8];
            return ProtocolVerdict.Yes(
                Protocol,
                $"answered with exception {code} ({ModbusExceptionText(code)}) - Modbus is there, the device just does not give its identity");
        }

        if (function != ReadDeviceId)
        {
            return ProtocolVerdict.Yes(Protocol, $"answered with function {function}");
        }

        string? identity = ReadObjects(reply[(MbapLength + 1)..]);
        return ProtocolVerdict.Yes(Protocol, identity ?? "answered Read Device Identification with no objects");
    }

    /// <summary>The meaning of a Modbus exception code, in the words a drive manual uses.</summary>
    public static string ModbusExceptionText(byte code) => code switch
    {
        1 => "illegal function",
        2 => "illegal data address",
        3 => "illegal data value",
        4 => "server device failure",
        5 => "acknowledge",
        6 => "server device busy",
        8 => "memory parity error",
        10 => "gateway path unavailable",
        11 => "gateway target device failed to respond",
        _ => "unknown exception",
    };

    /// <summary>
    /// After the function code: MEI type, read code, conformity, more-follows, next id, count, then
    /// (id, length, value) per object.
    /// </summary>
    private static string? ReadObjects(ReadOnlySpan<byte> pdu)
    {
        if (pdu.Length < 6 || pdu[0] != MeiDeviceId)
        {
            return null;
        }

        int count = pdu[5];
        int at = 6;
        var parts = new string?[3];

        for (int n = 0; n < count && at + 2 <= pdu.Length; n++)
        {
            byte id = pdu[at];
            int length = pdu[at + 1];
            at += 2;

            if (at + length > pdu.Length)
            {
                break;
            }

            if (id < parts.Length)
            {
                parts[id] = Encoding.ASCII.GetString(pdu.Slice(at, length)).Trim();
            }

            at += length;
        }

        string text = string.Join(" ", parts.Where(p => !string.IsNullOrEmpty(p)));
        return text.Length == 0 ? null : text;
    }
}
