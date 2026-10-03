using System.Net;

namespace NetControl.Core.Reachability;

/// <summary>
/// The one request that proves which protocol is behind an open TCP port, and how to read the
/// answer.
///
/// <para><b>An open port proves something accepted a connection, not what it is.</b> Port 502 open
/// on a managed switch is usually a firewall appliance answering everything; port 102 open on a PC
/// is very often a historian, not a PLC. So the service check, having connected, asks each known
/// port the smallest question that only the real protocol answers - and every question here is a
/// <b>read</b>: identity, a model name, a hello. None of them writes, starts a session that holds
/// resources on the device beyond the connection itself, or changes any state.</para>
///
/// <para>Implementations are pure: they build bytes and judge bytes. The socket belongs to
/// <see cref="ServiceProbe"/>, which is why every one of them can be tested against hand-built
/// frames with no device and no listener.</para>
/// </summary>
public interface IProtocolHandshake
{
    /// <summary>What a confirmed answer proves is there, e.g. "EtherNet/IP".</summary>
    string Protocol { get; }

    /// <summary>
    /// The request to send once connected. Empty for a protocol where the server speaks first -
    /// SSH and FTP announce themselves - in which case nothing is sent at all.
    /// </summary>
    byte[] BuildRequest(IPAddress address, int port);

    /// <summary>
    /// How long the whole reply is, once enough of it has arrived to say; null until then. The
    /// probe stops reading when it has this many bytes, the far end closes, or the reply timeout
    /// runs out - whichever comes first.
    /// </summary>
    int? ExpectedLength(ReadOnlySpan<byte> received);

    /// <summary>What the bytes that came back say. Never throws on junk: junk is a verdict.</summary>
    ProtocolVerdict Judge(ReadOnlySpan<byte> reply, IPAddress address);
}
