using System.Buffers.Binary;
using System.Net;

namespace NetControl.Core.Reachability;

/// <summary>
/// ISO-on-TCP (RFC 1006): a COTP connection request to rack 0, slot 2 - the transport every S7
/// conversation starts with. A connection confirm proves the transport; nothing S7 is sent after
/// it, so no communication session is set up and no job is placed on the CPU.
///
/// <para>A refusal (disconnect request) still proves ISO-on-TCP is there. An S7-1200 or -1500 often
/// refuses slot 2, and a CP in another slot will too; the verdict says which happened, so the
/// person reading it knows to try the slot in their project rather than concluding nothing is
/// there.</para>
/// </summary>
public sealed class IsoTsapHandshake : IProtocolHandshake
{
    private const byte Tpkt = 0x03;
    private const byte ConnectionConfirm = 0xD0;
    private const byte DisconnectRequest = 0x80;

    public string Protocol => "ISO-on-TCP (S7)";

    public byte[] BuildRequest(IPAddress address, int port) =>
    [
        // TPKT: version 3, reserved, total length 22.
        Tpkt, 0x00, 0x00, 0x16,

        // COTP connection request: length 17, CR, dst ref 0, src ref 1, class 0.
        0x11, 0xE0, 0x00, 0x00, 0x00, 0x01, 0x00,

        // Calling TSAP 01.00, called TSAP 01.02 (rack 0, slot 2), TPDU size 1024.
        0xC1, 0x02, 0x01, 0x00,
        0xC2, 0x02, 0x01, 0x02,
        0xC0, 0x01, 0x0A,
    ];

    public int? ExpectedLength(ReadOnlySpan<byte> received) =>
        received.Length < 4 || received[0] != Tpkt ? null : BinaryPrimitives.ReadUInt16BigEndian(received[2..]);

    public ProtocolVerdict Judge(ReadOnlySpan<byte> reply, IPAddress address)
    {
        if (reply.Length < 6 || reply[0] != Tpkt)
        {
            return ProtocolVerdict.Other(Protocol, "answered, but not with RFC 1006 framing");
        }

        return (reply[5] & 0xF0) switch
        {
            ConnectionConfirm => ProtocolVerdict.Yes(Protocol, "accepted a connection to rack 0, slot 2 - an S7 CPU or something speaking its transport"),
            DisconnectRequest => ProtocolVerdict.Yes(Protocol, "speaks ISO-on-TCP but refused rack 0, slot 2 - an S7-1200/1500 or a CP in another slot"),
            _ => ProtocolVerdict.Yes(Protocol, "answered with ISO-on-TCP framing"),
        };
    }
}
