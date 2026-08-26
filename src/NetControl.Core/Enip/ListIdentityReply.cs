using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Text;

namespace NetControl.Core.Enip;

/// <summary>
/// One device's answer to a ListIdentity broadcast: who it is, and where it says it lives.
///
/// <para><b>The address in here is big-endian sitting inside an otherwise little-endian
/// structure.</b> The identity item carries a <c>sockaddr_in</c> - the same layout a socket API
/// would hand you - while every other field around it is a little-endian CIP integer. Reading the
/// address the same way as its neighbours produces 51.1.168.192 for a device at 192.168.1.51,
/// which is plausible enough to be believed and wrong enough to send the next TCP connection into
/// nowhere. <see cref="ReadSockaddrAddress"/> exists so that conversion happens in one place.</para>
///
/// <para><b><see cref="Address"/> and <see cref="SenderAddress"/> are deliberately both kept.</b>
/// The first is what the device believes about itself; the second is where the datagram actually
/// came from. On a flat segment they agree. When they do not, something is worth knowing about -
/// a device answering through a router, a NAT in the path, or a device whose configuration and
/// wiring disagree - and collapsing them into one field throws that away.</para>
/// </summary>
public sealed record ListIdentityReply
{
    /// <summary>Encapsulation command 0x0063. The only one this type accepts.</summary>
    internal const ushort Command = 0x0063;

    /// <summary>CPF item type for an identity object, which is the only item a reply carries.</summary>
    private const ushort ItemListIdentity = 0x000C;

    private const int HeaderLength = 24;

    /// <summary>Encapsulation version, sockaddr_in, and the fixed identity fields before the name.</summary>
    private const int FixedBodyLength = 2 + 16 + 2 + 2 + 2 + 2 + 2 + 4;

    /// <summary>Where the datagram came from - the device as the wire saw it.</summary>
    public required IPEndPoint Sender { get; init; }

    /// <summary>The address the device reports for itself, out of the sockaddr_in.</summary>
    public required IPAddress Address { get; init; }

    /// <summary>The port in the sockaddr_in - 44818 on everything anyone has met, but it is data.</summary>
    public required int Port { get; init; }

    /// <summary>
    /// ODVA vendor id. 1 is Rockwell Automation. This is <em>not</em> the IEEE OUI and there is no
    /// relationship between the two numbering schemes; see <see cref="ProductName"/>, which is what
    /// this tool shows instead of maintaining a second vendor table.
    /// </summary>
    public required ushort VendorId { get; init; }

    public required ushort DeviceType { get; init; }

    public required ushort ProductCode { get; init; }

    public required byte MajorRevision { get; init; }

    public required byte MinorRevision { get; init; }

    public required ushort DeviceStatus { get; init; }

    public required uint SerialNumber { get; init; }

    public required string ProductName { get; init; }

    /// <summary>Identity object state. 3 is operational; 0 and 1 mean it is still coming up.</summary>
    public required byte State { get; init; }

    public IPAddress SenderAddress => Sender.Address;

    /// <summary>False when the device's own idea of its address is not where the reply came from.</summary>
    public bool AddressMatchesSender => Address.Equals(Sender.Address);

    public string Revision =>
        string.Create(CultureInfo.InvariantCulture, $"{MajorRevision}.{MinorRevision:00}");

    /// <summary>
    /// Serial numbers are quoted in hex on nameplates and in every vendor tool, so they are shown
    /// that way here too - a decimal serial number matches nothing the user can read off the module.
    /// </summary>
    public string SerialText => string.Create(CultureInfo.InvariantCulture, $"{SerialNumber:X8}");

    /// <summary>
    /// Parses one received datagram, or returns false if it is not a ListIdentity reply this code
    /// understands.
    ///
    /// <para>False rather than an exception on purpose: this runs over a receive loop on a
    /// broadcast port, so it will meet other tools' traffic, replies from a scan that has already
    /// finished, and the occasional device with its own ideas about framing. None of those is an
    /// error worth interrupting a scan for - the honest response is to ignore the datagram and
    /// count it.</para>
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> datagram, IPEndPoint sender, out ListIdentityReply? reply)
    {
        ArgumentNullException.ThrowIfNull(sender);

        reply = null;

        if (datagram.Length < HeaderLength)
        {
            return false;
        }

        if (BinaryPrimitives.ReadUInt16LittleEndian(datagram) != Command)
        {
            return false;
        }

        // A non-zero encapsulation status means the device is telling us it could not answer.
        // There is no identity in the frame to read, so there is nothing to report but the silence.
        if (BinaryPrimitives.ReadUInt32LittleEndian(datagram[8..]) != 0)
        {
            return false;
        }

        ushort length = BinaryPrimitives.ReadUInt16LittleEndian(datagram[2..]);
        if (length == 0 || datagram.Length < HeaderLength + length)
        {
            return false;
        }

        ReadOnlySpan<byte> items = datagram.Slice(HeaderLength, length);
        if (items.Length < 2)
        {
            return false;
        }

        ushort count = BinaryPrimitives.ReadUInt16LittleEndian(items);
        int at = 2;

        for (int n = 0; n < count; n++)
        {
            if (at + 4 > items.Length)
            {
                return false;
            }

            ushort type = BinaryPrimitives.ReadUInt16LittleEndian(items[at..]);
            ushort itemLength = BinaryPrimitives.ReadUInt16LittleEndian(items[(at + 2)..]);
            at += 4;

            if (at + itemLength > items.Length)
            {
                return false;
            }

            if (type == ItemListIdentity)
            {
                return TryParseIdentityItem(items.Slice(at, itemLength), sender, out reply);
            }

            at += itemLength;
        }

        return false;
    }

    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Address} '{ProductName}' rev {Revision} serial {SerialText} vendor {VendorId}");

    private static bool TryParseIdentityItem(ReadOnlySpan<byte> body, IPEndPoint sender, out ListIdentityReply? reply)
    {
        reply = null;

        if (body.Length < FixedBodyLength + 1)
        {
            return false;
        }

        // A CIP SHORT_STRING: one length byte, then that many ASCII characters. The state byte
        // follows it, and a device that truncated the frame is one whose name cannot be trusted.
        int nameLength = body[FixedBodyLength];
        if (body.Length < FixedBodyLength + 1 + nameLength)
        {
            return false;
        }

        string name = nameLength == 0
            ? string.Empty
            : Encoding.ASCII.GetString(body.Slice(FixedBodyLength + 1, nameLength)).Trim();

        // The state byte is the last thing in the item, and a few devices leave it off entirely.
        // Missing state is not a reason to discard an otherwise good identity.
        int stateAt = FixedBodyLength + 1 + nameLength;
        byte state = stateAt < body.Length ? body[stateAt] : (byte)0xFF;

        reply = new ListIdentityReply
        {
            Sender = sender,

            // body[0..2] is the encapsulation protocol version, and body[2..4] the sockaddr_in's
            // AF_INET. Neither tells us anything a caller can act on, so neither is carried.
            Port = BinaryPrimitives.ReadUInt16BigEndian(body[4..]),
            Address = ReadSockaddrAddress(body[6..]),

            VendorId = BinaryPrimitives.ReadUInt16LittleEndian(body[18..]),
            DeviceType = BinaryPrimitives.ReadUInt16LittleEndian(body[20..]),
            ProductCode = BinaryPrimitives.ReadUInt16LittleEndian(body[22..]),
            MajorRevision = body[24],
            MinorRevision = body[25],
            DeviceStatus = BinaryPrimitives.ReadUInt16LittleEndian(body[26..]),
            SerialNumber = BinaryPrimitives.ReadUInt32LittleEndian(body[28..]),
            ProductName = name,
            State = state,
        };

        return true;
    }

    /// <summary>
    /// The four octets of a <c>sockaddr_in</c>'s <c>sin_addr</c>, which are already in network
    /// order - so this is a straight copy, and the point of the method is that it is the only
    /// place allowed to be. Everything else in this structure is little-endian; the two must never
    /// be read by the same helper.
    /// </summary>
    private static IPAddress ReadSockaddrAddress(ReadOnlySpan<byte> source) =>
        new(source[..4]);
}
