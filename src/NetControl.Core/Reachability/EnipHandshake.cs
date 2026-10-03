using System.Buffers.Binary;
using System.Net;
using NetControl.Core.Enip;

namespace NetControl.Core.Reachability;

/// <summary>
/// EtherNet/IP ListIdentity, sent over the TCP connection the check already opened. The same
/// question the scan broadcasts, asked of one device: no session is registered, nothing is routed,
/// and the reply is parsed by <see cref="ListIdentityReply"/> - so the service check and the scan
/// cannot disagree about what a device said.
/// </summary>
public sealed class EnipHandshake : IProtocolHandshake
{
    private const int HeaderLength = 24;

    public string Protocol => "EtherNet/IP";

    public byte[] BuildRequest(IPAddress address, int port)
    {
        var frame = new byte[HeaderLength];
        BinaryPrimitives.WriteUInt16LittleEndian(frame, ListIdentityReply.Command);

        // Sender context: eight bytes the device echoes back. Anything will do; this says who asked.
        "NetCtrl\0"u8.CopyTo(frame.AsSpan(12));
        return frame;
    }

    public int? ExpectedLength(ReadOnlySpan<byte> received) =>
        received.Length < 4 ? null : HeaderLength + BinaryPrimitives.ReadUInt16LittleEndian(received[2..]);

    public ProtocolVerdict Judge(ReadOnlySpan<byte> reply, IPAddress address)
    {
        if (ListIdentityReply.TryParse(reply, new IPEndPoint(address, 44818), out ListIdentityReply? identity)
            && identity is not null)
        {
            return ProtocolVerdict.Yes(
                Protocol,
                $"{identity.ProductName}, rev {identity.Revision}, serial {identity.SerialText}");
        }

        if (reply.Length >= 2 && BinaryPrimitives.ReadUInt16LittleEndian(reply) == ListIdentityReply.Command)
        {
            return ProtocolVerdict.Yes(Protocol, "answered ListIdentity, but the identity in it could not be read");
        }

        return ProtocolVerdict.Other(Protocol, "answered, but not with an EtherNet/IP encapsulation header");
    }
}
