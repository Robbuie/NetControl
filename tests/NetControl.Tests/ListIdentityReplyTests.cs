using System.Buffers.Binary;
using System.Net;
using System.Text;
using NetControl.Core.Enip;
using Xunit;

namespace NetControl.Tests;

/// <summary>
/// The ListIdentity reply codec, against frames built byte by byte here rather than produced by
/// anything in the source tree.
///
/// <para>The simulator was written from the same specification as the client, so a test that only
/// drives one against the other would pass just as happily if both had misread the spec the same
/// way. These frames are the independent check on that, and the field they exist for is the
/// address: a <c>sockaddr_in</c> sitting big-endian inside an otherwise little-endian structure,
/// which read the wrong way turns 192.168.1.51 into 51.1.168.192 - an address that is valid,
/// plausible, and nowhere near the device.</para>
/// </summary>
public class ListIdentityReplyTests
{
    private static readonly IPEndPoint Sender = new(IPAddress.Parse("192.168.1.51"), 44818);

    /// <summary>The one assertion this whole file exists for.</summary>
    [Fact]
    public void ReadsTheAddressBigEndianOutOfAnOtherwiseLittleEndianStructure()
    {
        Assert.True(ListIdentityReply.TryParse(Reply(address: "192.168.1.51"), Sender, out ListIdentityReply? reply));

        Assert.NotNull(reply);
        Assert.Equal(IPAddress.Parse("192.168.1.51"), reply.Address);

        // Stated the wrong way round too, so that a "fix" which reverses the read fails here
        // rather than in a panel.
        Assert.NotEqual(IPAddress.Parse("51.1.168.192"), reply.Address);
    }

    [Fact]
    public void ReadsTheIdentityFieldsLittleEndian()
    {
        Assert.True(ListIdentityReply.TryParse(Reply(), Sender, out ListIdentityReply? reply));

        Assert.NotNull(reply);
        Assert.Equal(1, reply.VendorId);
        Assert.Equal(12, reply.DeviceType);
        Assert.Equal(167, reply.ProductCode);
        Assert.Equal("11.03", reply.Revision);
        Assert.Equal(0x00C0FFEEu, reply.SerialNumber);
        Assert.Equal("00C0FFEE", reply.SerialText);
        Assert.Equal("1734-AENT/B", reply.ProductName);
        Assert.Equal(44818, reply.Port);
        Assert.Equal(3, reply.State);
    }

    /// <summary>
    /// The device's claim about itself and the place the datagram came from are different facts.
    /// Keeping both is what lets the tool say "that device thinks it is somewhere else", which is
    /// a real state on a segment with a duplicated address or a router in the path.
    /// </summary>
    [Fact]
    public void KeepsWhatTheDeviceClaimsApartFromWhereItAnsweredFrom()
    {
        var elsewhere = new IPEndPoint(IPAddress.Parse("192.168.1.77"), 44818);

        Assert.True(
            ListIdentityReply.TryParse(Reply(address: "192.168.1.51"), elsewhere, out ListIdentityReply? reply));

        Assert.NotNull(reply);
        Assert.Equal(IPAddress.Parse("192.168.1.51"), reply.Address);
        Assert.Equal(IPAddress.Parse("192.168.1.77"), reply.SenderAddress);
        Assert.False(reply.AddressMatchesSender);
    }

    /// <summary>
    /// A scan listens on a broadcast port, so it meets other tools' traffic. Anything that is not
    /// a ListIdentity reply is ignored and counted, never thrown over - one stray datagram must
    /// not end a scan that is halfway through finding a panel.
    /// </summary>
    [Theory]
    [InlineData(0x0065)]    // RegisterSession, which is TCP-only and has no business here
    [InlineData(0x0004)]    // ListServices
    public void IgnoresADatagramThatIsNotAListIdentityReply(int command)
    {
        Assert.False(ListIdentityReply.TryParse(Reply(command: (ushort)command), Sender, out ListIdentityReply? reply));
        Assert.Null(reply);
    }

    /// <summary>A device saying "I cannot answer that" has put no identity in the frame to read.</summary>
    [Fact]
    public void IgnoresAReplyWhoseEncapsulationStatusIsNotZero()
    {
        Assert.False(ListIdentityReply.TryParse(Reply(status: 0x0001), Sender, out ListIdentityReply? reply));
        Assert.Null(reply);
    }

    [Fact]
    public void IgnoresAFrameTooShortToHoldWhatItClaims()
    {
        byte[] full = Reply();

        Assert.False(ListIdentityReply.TryParse(full.AsSpan(0, full.Length - 8), Sender, out ListIdentityReply? reply));
        Assert.Null(reply);
    }

    [Fact]
    public void IgnoresAReplyCarryingNoIdentityItem()
    {
        // A well-formed header and CPF with an item count of zero: legal framing, nothing in it.
        var frame = new byte[24 + 2];
        BinaryPrimitives.WriteUInt16LittleEndian(frame, 0x0063);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2), 2);

        Assert.False(ListIdentityReply.TryParse(frame, Sender, out ListIdentityReply? reply));
        Assert.Null(reply);
    }

    /// <summary>
    /// Product names come back padded on several adapters. A trailing space is not part of a
    /// device's name and would show up in the grid, in a CSV export and in a report.
    /// </summary>
    [Fact]
    public void TrimsAPaddedProductName()
    {
        Assert.True(ListIdentityReply.TryParse(Reply(name: "PowerFlex 525  "), Sender, out ListIdentityReply? reply));

        Assert.NotNull(reply);
        Assert.Equal("PowerFlex 525", reply.ProductName);
    }

    /// <summary>
    /// One reply, built the way a Rockwell adapter emits it: little-endian everywhere except the
    /// sockaddr_in, which is big-endian.
    /// </summary>
    private static byte[] Reply(
        string address = "192.168.1.51",
        int port = 44818,
        string name = "1734-AENT/B",
        ushort command = 0x0063,
        uint status = 0)
    {
        byte[] nameBytes = Encoding.ASCII.GetBytes(name);
        int bodyLength = 32 + 1 + nameBytes.Length + 1;
        int itemsLength = 2 + 4 + bodyLength;

        var frame = new byte[24 + itemsLength];
        Span<byte> f = frame;

        BinaryPrimitives.WriteUInt16LittleEndian(f, command);
        BinaryPrimitives.WriteUInt16LittleEndian(f[2..], (ushort)itemsLength);
        BinaryPrimitives.WriteUInt32LittleEndian(f[4..], 0);             // session handle, zero on discovery
        BinaryPrimitives.WriteUInt32LittleEndian(f[8..], status);

        Span<byte> items = f[24..];
        BinaryPrimitives.WriteUInt16LittleEndian(items, 1);              // item count
        BinaryPrimitives.WriteUInt16LittleEndian(items[2..], 0x000C);    // identity item
        BinaryPrimitives.WriteUInt16LittleEndian(items[4..], (ushort)bodyLength);

        Span<byte> body = items[6..];
        BinaryPrimitives.WriteUInt16LittleEndian(body, 1);               // encapsulation version

        // sockaddr_in: big-endian, in the middle of all the above.
        BinaryPrimitives.WriteInt16BigEndian(body[2..], 2);              // AF_INET
        BinaryPrimitives.WriteUInt16BigEndian(body[4..], (ushort)port);
        IPAddress.Parse(address).GetAddressBytes().CopyTo(body[6..]);

        BinaryPrimitives.WriteUInt16LittleEndian(body[18..], 1);         // vendor: Rockwell
        BinaryPrimitives.WriteUInt16LittleEndian(body[20..], 12);        // communications adapter
        BinaryPrimitives.WriteUInt16LittleEndian(body[22..], 167);
        body[24] = 11;
        body[25] = 3;
        BinaryPrimitives.WriteUInt16LittleEndian(body[26..], 0x0030);
        BinaryPrimitives.WriteUInt32LittleEndian(body[28..], 0x00C0FFEE);
        body[32] = (byte)nameBytes.Length;
        nameBytes.CopyTo(body[33..]);
        body[33 + nameBytes.Length] = 3;                                 // operational

        return frame;
    }
}
