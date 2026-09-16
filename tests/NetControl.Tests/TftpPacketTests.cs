using System.Text;
using NetControl.Core.Tftp;
using Xunit;

namespace NetControl.Tests;

/// <summary>
/// The codec, against hand-built frames.
///
/// <para>Every case here was first run through a Python port of <see cref="TftpPacket"/> in a
/// session with no SDK - the same technique that found the stray-quote bug in
/// <c>CsvFile.Parse</c>. The port found one thing, and it was a wrong claim rather than wrong
/// code: see <see cref="TftpLimitsTests.NegotiatingTheLargestUnfragmentedBlockDoesNotSaveALargeImage"/>.</para>
/// </summary>
public class TftpPacketTests
{
    [Fact]
    public void ParsesAPlainWriteRequest()
    {
        Assert.True(TftpPacket.TryParse(TftpFrames.PlainWriteRequest(), out TftpMessage? message, out string? problem));
        Assert.Null(problem);

        var request = Assert.IsType<TftpRequestMessage>(message);
        Assert.Equal(TftpOpcode.WriteRequest, request.Opcode);
        Assert.True(request.IsWrite);
        Assert.Equal("FROM00.IMG", request.FileName);
        Assert.Equal(TftpTransferMode.Octet, request.Mode);
        Assert.True(request.Options.IsEmpty);
    }

    [Fact]
    public void ParsesAWriteRequestWithItsOptionsInTheOrderTheyWereOffered()
    {
        Assert.True(TftpPacket.TryParse(TftpFrames.NegotiatingWriteRequest(), out TftpMessage? message, out _));

        var request = Assert.IsType<TftpRequestMessage>(message);
        Assert.Equal(3, request.Options.Count);
        Assert.Equal(
            new[] { "blksize", "tsize", "timeout" },
            request.Options.All.Select(option => option.Name).ToArray());
        Assert.Equal(1468, request.Options.BlockSize);
        Assert.Equal(134217728L, request.Options.TransferSize);
        Assert.Equal(5, request.Options.TimeoutSeconds);
    }

    [Theory]
    [InlineData(@"\md:\FROM00.IMG")]
    [InlineData("/backup/robot 7/FROM00.IMG")]
    [InlineData(@"C:\images\a.img")]
    [InlineData("a/../b")]
    [InlineData("")]
    public void KeepsTheFilenameExactlyAsSent(string name)
    {
        // The whole point of watch mode. A controller asking for a path the server does not have
        // is a five-second fix, and no TFTP server logs the name it refused - so this tool must
        // not be the second thing that quietly rewrites it.
        Assert.True(TftpPacket.TryParse(
            TftpFrames.Request((ushort)TftpOpcode.WriteRequest, name, "octet"),
            out TftpMessage? message,
            out _));

        Assert.Equal(name, Assert.IsType<TftpRequestMessage>(message).FileName);
    }

    [Fact]
    public void KeepsAFilenameThatAsciiWouldDestroy()
    {
        // Encoding.ASCII turns every byte above 0x7F into a question mark. Latin-1 round-trips
        // all 256, which is why the codec uses it.
        const string name = "sauvegarde-\u00e9t\u00e9.img";

        Assert.True(TftpPacket.TryParse(
            TftpFrames.Request((ushort)TftpOpcode.WriteRequest, name, "octet"),
            out TftpMessage? message,
            out _));

        var request = Assert.IsType<TftpRequestMessage>(message);
        Assert.Equal(name, request.FileName);
        Assert.Equal(Encoding.Latin1.GetBytes(name), request.FileNameBytes);
    }

    [Theory]
    [InlineData("octet")]
    [InlineData("OCTET")]
    [InlineData("Octet")]
    [InlineData("oCtEt")]
    public void ReadsTheTransferModeCaseInsensitivelyAndStillKeepsTheRawString(string token)
    {
        Assert.True(TftpPacket.TryParse(
            TftpFrames.Request((ushort)TftpOpcode.WriteRequest, "a.img", token),
            out TftpMessage? message,
            out _));

        var request = Assert.IsType<TftpRequestMessage>(message);
        Assert.Equal(TftpTransferMode.Octet, request.Mode);
        Assert.Equal(token, request.RawMode);
    }

    [Fact]
    public void ParsesNetasciiRatherThanRefusingItSoItCanBeWarnedAbout()
    {
        // An image asked for in netascii arrives corrupted with nothing reporting an error. The
        // codec's job is to make that visible, not to reject the packet.
        Assert.True(TftpPacket.TryParse(
            TftpFrames.Request((ushort)TftpOpcode.WriteRequest, "FROM00.IMG", "netascii"),
            out TftpMessage? message,
            out _));

        var request = Assert.IsType<TftpRequestMessage>(message);
        Assert.Equal(TftpTransferMode.NetAscii, request.Mode);
        Assert.True(request.ModeWouldCorruptBinary);
        Assert.Contains(request.Concerns(), concern => concern.Contains("corrupted", StringComparison.Ordinal));
    }

    [Fact]
    public void ReportsAnUnrecognisedModeWithoutGuessingAtIt()
    {
        Assert.True(TftpPacket.TryParse(
            TftpFrames.Request((ushort)TftpOpcode.WriteRequest, "a.img", "binary"),
            out TftpMessage? message,
            out _));

        var request = Assert.IsType<TftpRequestMessage>(message);
        Assert.Equal(TftpTransferMode.Unknown, request.Mode);
        Assert.Equal("binary", request.RawMode);
    }

    [Fact]
    public void ReadsBlockNumbersBigEndian()
    {
        // TFTP is big-endian and the CIP half of this product is little-endian. This assertion
        // guards the most expensive mistake available in this codebase.
        Assert.Equal(new byte[] { 0x00, 0x04, 0x01, 0x00 }, TftpPacket.EncodeAck(256));

        // Opcode 3, block 258 as 0x0102, then the payload - the whole frame, not just its header.
        Assert.Equal(new byte[] { 0x00, 0x03, 0x01, 0x02, 0x78, 0x79 }, TftpPacket.EncodeData(258, "xy"u8));

        Assert.True(TftpPacket.TryParse(TftpPacket.EncodeAck(65535), out TftpMessage? message, out _));
        Assert.Equal(65535, Assert.IsType<TftpAckMessage>(message).Block);
    }

    [Fact]
    public void ParsesAZeroLengthDataBlock()
    {
        // Not malformed: it is how a file whose length is an exact multiple of the block size
        // announces its own end. Rejecting it turns every exactly-sized file into a mystery.
        Assert.True(TftpPacket.TryParse(TftpPacket.EncodeData(66, []), out TftpMessage? message, out _));

        var data = Assert.IsType<TftpDataMessage>(message);
        Assert.Empty(data.Data);
        Assert.True(data.IsFinal(TftpLimits.DefaultBlockSize));
    }

    [Fact]
    public void AShortBlockIsOnlyFinalRelativeToTheNegotiatedSize()
    {
        // A receiver that assumes 512 while the sender was granted 1468 truncates the file.
        var data = new TftpDataMessage(1, new byte[600]);

        Assert.False(data.IsFinal(TftpLimits.DefaultBlockSize));
        Assert.True(data.IsFinal(1468));
    }

    [Fact]
    public void TakesTheWholeMessageFromAnErrorThatWasNeverNullTerminated()
    {
        // Some servers omit the terminator. That string is frequently the only specific thing in
        // the packet, so discarding it would be the wrong kind of strictness.
        byte[] frame = [0x00, 0x05, 0x00, 0x02, .. "no room"u8];

        Assert.True(TftpPacket.TryParse(frame, out TftpMessage? message, out _));

        var error = Assert.IsType<TftpErrorMessage>(message);
        Assert.Equal(TftpErrorCode.AccessViolation, error.Code);
        Assert.Equal("no room", error.Message);
    }

    [Fact]
    public void ParsesAnErrorThatCarriesNoMessageAtAll()
    {
        Assert.True(TftpPacket.TryParse([0x00, 0x05, 0x00, 0x01], out TftpMessage? message, out _));

        var error = Assert.IsType<TftpErrorMessage>(message);
        Assert.Equal(TftpErrorCode.FileNotFound, error.Code);
        Assert.Equal(string.Empty, error.Message);
    }

    [Fact]
    public void IgnoresZeroPaddingAfterTheOptions()
    {
        byte[] frame =
        [
            .. TftpFrames.Request((ushort)TftpOpcode.WriteRequest, "a.img", "octet", "blksize", "512"),
            0, 0, 0, 0,
        ];

        Assert.True(TftpPacket.TryParse(frame, out TftpMessage? message, out string? problem));
        Assert.Null(problem);

        var request = Assert.IsType<TftpRequestMessage>(message);
        Assert.Equal(1, request.Options.Count);
        Assert.Equal(512, request.Options.BlockSize);
    }

    [Fact]
    public void IgnoresPaddingAfterAnAck()
    {
        byte[] frame = [0x00, 0x04, 0x00, 0x03, .. new byte[42]];

        Assert.True(TftpPacket.TryParse(frame, out TftpMessage? message, out _));
        Assert.Equal(3, Assert.IsType<TftpAckMessage>(message).Block);
    }

    [Fact]
    public void RefusesADuplicateOptionEvenInADifferentCase()
    {
        // Option names are case-insensitive per RFC 2347. Letting one of two silently win is the
        // same mistake the CSV importer refuses for a header naming one column twice: the tool
        // would then report a setting nobody sent.
        byte[] frame = TftpFrames.Request(
            (ushort)TftpOpcode.WriteRequest, "a.img", "octet", "blksize", "512", "BLKSIZE", "1468");

        Assert.False(TftpPacket.TryParse(frame, out TftpMessage? message, out string? problem));
        Assert.Null(message);
        Assert.NotNull(problem);
        Assert.Contains("more than once", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new byte[] { }, "two-octet opcode")]
    [InlineData(new byte[] { 0x00 }, "two-octet opcode")]
    [InlineData(new byte[] { 0x00, 0x00 }, "not one of the six")]
    [InlineData(new byte[] { 0x00, 0x07 }, "not one of the six")]
    [InlineData(new byte[] { 0x00, 0x03, 0x01 }, "two-octet block number")]
    [InlineData(new byte[] { 0x00, 0x04, 0x01 }, "two-octet block number")]
    [InlineData(new byte[] { 0x00, 0x05, 0x01 }, "two-octet error code")]
    public void RefusesWhatCannotBeATftpPacketAndSaysWhy(byte[] frame, string reason)
    {
        Assert.False(TftpPacket.TryParse(frame, out TftpMessage? message, out string? problem));
        Assert.Null(message);
        Assert.NotNull(problem);
        Assert.Contains(reason, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesARequestWhoseFilenameIsNeverTerminated()
    {
        byte[] frame = [0x00, 0x02, .. "FROM00.IMG"u8];

        Assert.False(TftpPacket.TryParse(frame, out _, out string? problem));
        Assert.NotNull(problem);
        Assert.Contains("not null-terminated", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesARequestWithNoTransferMode()
    {
        byte[] frame = [0x00, 0x02, .. "a.img"u8, 0x00];

        Assert.False(TftpPacket.TryParse(frame, out _, out string? problem));
        Assert.NotNull(problem);
        Assert.Contains("mode", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesAnOptionWithNoValue()
    {
        // opcode 2, "a.img", "octet", then an option name with the packet ending after it.
        byte[] frame = [0x00, 0x02, .. "a.img"u8, 0x00, .. "octet"u8, 0x00, .. "blksize"u8, 0x00];

        Assert.False(TftpPacket.TryParse(frame, out _, out string? problem));
        Assert.NotNull(problem);
        Assert.Contains("has no value", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesAnEmptyOptionNameWhenSomethingFollowsIt()
    {
        // Trailing zeros are padding and are tolerated. An empty name with a value after it is a
        // real malformation and is reported rather than skipped.
        byte[] frame = [0x00, 0x02, .. "a.img"u8, 0x00, .. "octet"u8, 0x00, 0x00, .. "512"u8, 0x00];

        Assert.False(TftpPacket.TryParse(frame, out _, out string? problem));
        Assert.NotNull(problem);
        Assert.Contains("empty name", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(511)]
    [InlineData(512)]
    [InlineData(1468)]
    [InlineData(TftpLimits.MaxBlockSize)]
    public void RoundTripsADataBlockOfEverySizeThatMatters(int size)
    {
        byte[] payload = new byte[size];
        Array.Fill(payload, (byte)0xAB);

        byte[] frame = TftpPacket.EncodeData(1, payload);
        Assert.Equal(size + TftpPacket.DataHeaderLength, frame.Length);

        Assert.True(TftpPacket.TryParse(frame, out TftpMessage? message, out _));
        Assert.Equal(payload, Assert.IsType<TftpDataMessage>(message).Data);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(8, 4)]
    [InlineData(255, 0)]
    public void EncodesARequestAtExactlyTheLengthItMeasured(int nameLength, int optionCount)
    {
        // The encoder computes a buffer length and then writes into it. If those two ever
        // disagree the failure is a trailing zero or an IndexOutOfRange, neither of which says
        // what went wrong - so the round trip is asserted over a range of shapes.
        var options = new List<TftpOption>();
        for (int i = 0; i < optionCount; i++)
        {
            options.Add(new TftpOption($"o{i}", new string('v', i)));
        }

        byte[] frame = TftpPacket.EncodeRequest(
            TftpOpcode.WriteRequest,
            new string('x', nameLength),
            TftpTransferMode.Octet,
            new TftpOptions(options));

        Assert.True(TftpPacket.TryParse(frame, out TftpMessage? message, out string? problem));
        Assert.Null(problem);

        var request = Assert.IsType<TftpRequestMessage>(message);
        Assert.Equal(new string('x', nameLength), request.FileName);
        Assert.Equal(optionCount, request.Options.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("x")]
    [InlineData("Access violation")]
    public void RoundTripsAnErrorMessage(string text)
    {
        Assert.True(TftpPacket.TryParse(
            TftpPacket.EncodeError(TftpErrorCode.AccessViolation, text),
            out TftpMessage? message,
            out _));

        Assert.Equal(text, Assert.IsType<TftpErrorMessage>(message).Message);
    }

    [Fact]
    public void RoundTripsAnOptionAcknowledgement()
    {
        var granted = new TftpOptions([new TftpOption("blksize", "512")]);

        Assert.True(TftpPacket.TryParse(TftpPacket.EncodeOptionAck(granted), out TftpMessage? message, out _));

        var oack = Assert.IsType<TftpOptionAckMessage>(message);
        Assert.Equal(512, oack.Options.BlockSize);
    }

    [Fact]
    public void RefusesToEncodeAStringHoldingANull()
    {
        // It would terminate the field early and produce a packet that parses cleanly into
        // something nobody asked for.
        Assert.Throws<ArgumentException>(() =>
            TftpPacket.EncodeRequest(TftpOpcode.WriteRequest, "a\0b.img", TftpTransferMode.Octet));
    }

    [Fact]
    public void RefusesToEncodeARequestWithNoTransferMode()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TftpPacket.EncodeRequest(TftpOpcode.WriteRequest, "a.img", TftpTransferMode.Unknown));
    }
}
