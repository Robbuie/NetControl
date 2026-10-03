using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace NetControl.Core.Reachability;

/// <summary>
/// Mitsubishi MC protocol, 3E frame, binary: Read CPU Model (command 0101). The one MC command
/// that touches no device memory at all. The port is a setting on the Ethernet module rather than a
/// standard, so only 5007 - the MELSOFT default - is asked by default.
///
/// <para>An end code other than zero still confirms MC: only an MC server answers a 3E frame with a
/// D0 00 subheader.</para>
/// </summary>
public sealed class MelsecHandshake : IProtocolHandshake
{
    private const int ResponseHeaderLength = 9;
    private const int ModelLength = 16;

    public string Protocol => "MELSEC MC";

    public byte[] BuildRequest(IPAddress address, int port) =>
    [
        0x50, 0x00,         // subheader: 3E request
        0x00,               // network 0
        0xFF,               // PC 255: the station this connection is to
        0xFF, 0x03,         // request destination module: 03FF, own CPU
        0x00,               // multidrop station 0
        0x06, 0x00,         // request data length: timer + command + subcommand
        0x10, 0x00,         // monitoring timer: 16 x 250 ms
        0x01, 0x01,         // command 0101: Read CPU model
        0x00, 0x00,         // subcommand 0000
    ];

    public int? ExpectedLength(ReadOnlySpan<byte> received) =>
        received.Length < ResponseHeaderLength || received[0] != 0xD0 || received[1] != 0x00
            ? null
            : ResponseHeaderLength + BinaryPrimitives.ReadUInt16LittleEndian(received[7..]);

    public ProtocolVerdict Judge(ReadOnlySpan<byte> reply, IPAddress address)
    {
        if (reply.Length < ResponseHeaderLength + 2 || reply[0] != 0xD0 || reply[1] != 0x00)
        {
            return ProtocolVerdict.Other(Protocol, "answered, but not with an MC 3E response");
        }

        ushort endCode = BinaryPrimitives.ReadUInt16LittleEndian(reply[ResponseHeaderLength..]);

        if (endCode != 0)
        {
            return ProtocolVerdict.Yes(Protocol, $"answered with end code {endCode:X4} - MC is there, Read CPU model was refused");
        }

        int modelAt = ResponseHeaderLength + 2;
        if (reply.Length < modelAt + ModelLength)
        {
            return ProtocolVerdict.Yes(Protocol, "answered Read CPU model with no model in it");
        }

        string model = Encoding.ASCII.GetString(reply.Slice(modelAt, ModelLength)).Trim(' ', '\0');
        return ProtocolVerdict.Yes(Protocol, model.Length == 0 ? "CPU model not reported" : $"CPU {model}");
    }
}
