using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace NetControl.Core.Reachability;

/// <summary>
/// OPC UA binary: a Hello. The server answers Acknowledge, or Error if it dislikes the endpoint
/// URL - and either proves an OPC UA stack. No secure channel is opened, so no session counts
/// against the server's limit.
/// </summary>
public sealed class OpcUaHandshake : IProtocolHandshake
{
    private const int HeaderLength = 8;

    /// <summary>Cap on the length the server claims, so a junk header cannot ask for megabytes.</summary>
    private const int MaxReply = 4096;

    public string Protocol => "OPC UA";

    public byte[] BuildRequest(IPAddress address, int port)
    {
        byte[] url = Encoding.UTF8.GetBytes($"opc.tcp://{address}:{port}");
        int length = HeaderLength + 20 + 4 + url.Length;
        var frame = new byte[length];

        "HELF"u8.CopyTo(frame);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), length);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(8), 0);        // protocol version
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(12), 65536);   // receive buffer
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(16), 65536);   // send buffer
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(20), 0);       // max message: no limit
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(24), 0);       // max chunks: no limit
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(28), url.Length);
        url.CopyTo(frame.AsSpan(32));
        return frame;
    }

    public int? ExpectedLength(ReadOnlySpan<byte> received) =>
        received.Length < HeaderLength
            ? null
            : Math.Clamp(BinaryPrimitives.ReadInt32LittleEndian(received[4..]), HeaderLength, MaxReply);

    public ProtocolVerdict Judge(ReadOnlySpan<byte> reply, IPAddress address)
    {
        if (reply.Length < HeaderLength)
        {
            return ProtocolVerdict.Other(Protocol, "answered too briefly to be an OPC UA message");
        }

        if (reply[..3].SequenceEqual("ACK"u8))
        {
            return ProtocolVerdict.Yes(Protocol, "acknowledged a Hello");
        }

        if (reply[..3].SequenceEqual("ERR"u8))
        {
            uint code = reply.Length >= 12 ? BinaryPrimitives.ReadUInt32LittleEndian(reply[8..]) : 0;
            return ProtocolVerdict.Yes(Protocol, $"refused the Hello with status 0x{code:X8} - an OPC UA server, unhappy with the endpoint URL");
        }

        return ProtocolVerdict.Other(Protocol, "answered, but not with an OPC UA message");
    }
}
