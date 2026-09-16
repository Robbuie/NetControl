using System.Buffers.Binary;
using System.Text;

namespace NetControl.Core.Tftp;

/// <summary>
/// The RFC 1350 codec, with the RFC 2347/2348/2349/7440 option extensions.
///
/// <para>Everything on this wire is BIG-endian - the opcode, the block number, the error code -
/// which is worth stating loudly because the EtherNet/IP and CIP halves of this product are
/// little-endian and now sit in the same solution. This type therefore never uses
/// <see cref="BitConverter"/>: every multi-byte read and write names its order at the call site
/// through <see cref="BinaryPrimitives"/>.</para>
///
/// <para>Parsing and encoding are synchronous and span-based, and they stay that way. They are
/// called from an async receive loop, and <c>Span&lt;T&gt;</c> locals are illegal in async
/// methods (CS4013), so the codec lives here in plain static helpers exactly as
/// <c>BootpPacket</c> does.</para>
///
/// <para><b>Strings are decoded as Latin-1, not ASCII.</b> RFC 1350 says netascii and real
/// equipment sends whatever it likes. <see cref="Encoding.ASCII"/> replaces every byte above
/// 0x7F with a question mark, which would quietly rewrite the one thing this module exists to
/// report accurately - the filename a controller actually asked for. Latin-1 round-trips all 256
/// byte values, so a name that will not print still comes back byte-for-byte, and
/// <see cref="TftpRequestMessage.FileNameBytes"/> carries the original beside it.</para>
/// </summary>
public static class TftpPacket
{
    /// <summary>Every packet starts with a two-byte opcode.</summary>
    public const int OpcodeLength = 2;

    /// <summary>Opcode plus block number: the fixed part of a DATA or ACK.</summary>
    public const int DataHeaderLength = 4;

    /// <summary>
    /// The largest datagram this protocol can produce: a four-octet header plus the biggest block
    /// RFC 2348 allows.
    ///
    /// <para>A receive buffer smaller than this truncates a large negotiated block <i>silently</i>
    /// - the read succeeds, the block looks short, and a short block means end-of-file, so the
    /// transfer completes and the file is wrong. Size every receive buffer from this constant.</para>
    /// </summary>
    public const int MaxDatagramLength = DataHeaderLength + TftpLimits.MaxBlockSize;

    private const string NetAsciiToken = "netascii";
    private const string OctetToken = "octet";
    private const string MailToken = "mail";

    /// <summary>
    /// Decodes one received datagram.
    ///
    /// <para>Returns false for anything that cannot be read as a TFTP packet at all, and sets
    /// <paramref name="problem"/> to a sentence saying why. That sentence is not decoration: a
    /// malformed datagram arriving on UDP/69 is itself a finding, and "something unparseable
    /// arrived" is a much weaker thing to tell somebody than "the filename in that request was
    /// never null-terminated".</para>
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> datagram, out TftpMessage? message, out string? problem)
    {
        message = null;
        problem = null;

        if (datagram.Length < OpcodeLength)
        {
            problem = $"Only {datagram.Length} octet(s) arrived; a TFTP packet starts with a "
                + "two-octet opcode.";
            return false;
        }

        ushort rawOpcode = BinaryPrimitives.ReadUInt16BigEndian(datagram);

        switch ((TftpOpcode)rawOpcode)
        {
            case TftpOpcode.ReadRequest:
            case TftpOpcode.WriteRequest:
                return TryParseRequest(datagram, (TftpOpcode)rawOpcode, out message, out problem);

            case TftpOpcode.Data:
                return TryParseData(datagram, out message, out problem);

            case TftpOpcode.Acknowledgement:
                return TryParseAck(datagram, out message, out problem);

            case TftpOpcode.Error:
                return TryParseError(datagram, out message, out problem);

            case TftpOpcode.OptionAcknowledgement:
                if (!TryParseOptions(datagram, OpcodeLength, out TftpOptions oackOptions, out problem))
                {
                    return false;
                }

                message = new TftpOptionAckMessage(oackOptions);
                return true;

            default:
                problem = $"Opcode {rawOpcode} is not one of the six TFTP opcodes (1-6). This is not a "
                    + "TFTP packet, or not one this tool knows.";
                return false;
        }
    }

    /// <summary>Builds a read or write request, with any options to offer.</summary>
    public static byte[] EncodeRequest(
        TftpOpcode opcode,
        string fileName,
        TftpTransferMode mode,
        TftpOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(fileName);

        if (opcode is not (TftpOpcode.ReadRequest or TftpOpcode.WriteRequest))
        {
            throw new ArgumentOutOfRangeException(
                nameof(opcode),
                opcode,
                "Only a read request or a write request carries a filename.");
        }

        if (mode == TftpTransferMode.Unknown)
        {
            throw new ArgumentOutOfRangeException(
                nameof(mode),
                mode,
                "A request has to name a transfer mode. Use Octet for anything binary.");
        }

        byte[] name = EncodeString(fileName, nameof(fileName));
        byte[] modeToken = EncodeString(ModeToken(mode), nameof(mode));
        TftpOptions offered = options ?? TftpOptions.None;

        int length = OpcodeLength + name.Length + 1 + modeToken.Length + 1 + MeasureOptions(offered);
        var buffer = new byte[length];

        BinaryPrimitives.WriteUInt16BigEndian(buffer, (ushort)opcode);

        int at = OpcodeLength;
        at = WriteNulTerminated(buffer, at, name);
        at = WriteNulTerminated(buffer, at, modeToken);
        WriteOptions(buffer, at, offered);

        return buffer;
    }

    /// <summary>Builds one DATA block. An empty payload is legal and ends a transfer.</summary>
    public static byte[] EncodeData(ushort block, ReadOnlySpan<byte> data)
    {
        var buffer = new byte[DataHeaderLength + data.Length];

        BinaryPrimitives.WriteUInt16BigEndian(buffer, (ushort)TftpOpcode.Data);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(OpcodeLength), block);
        data.CopyTo(buffer.AsSpan(DataHeaderLength));

        return buffer;
    }

    /// <summary>Builds an ACK. Block 0 accepts a write request.</summary>
    public static byte[] EncodeAck(ushort block)
    {
        var buffer = new byte[DataHeaderLength];

        BinaryPrimitives.WriteUInt16BigEndian(buffer, (ushort)TftpOpcode.Acknowledgement);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(OpcodeLength), block);

        return buffer;
    }

    /// <summary>
    /// Builds an ERROR. This is how watch mode refuses a transfer: a refusal the far end
    /// understands, rather than the silence that makes a controller retransmit for a minute.
    /// </summary>
    public static byte[] EncodeError(TftpErrorCode code, string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        byte[] text = EncodeString(message, nameof(message));
        var buffer = new byte[DataHeaderLength + text.Length + 1];

        BinaryPrimitives.WriteUInt16BigEndian(buffer, (ushort)TftpOpcode.Error);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(OpcodeLength), (ushort)code);
        WriteNulTerminated(buffer, DataHeaderLength, text);

        return buffer;
    }

    /// <summary>Builds an OACK naming the options being accepted.</summary>
    public static byte[] EncodeOptionAck(TftpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var buffer = new byte[OpcodeLength + MeasureOptions(options)];

        BinaryPrimitives.WriteUInt16BigEndian(buffer, (ushort)TftpOpcode.OptionAcknowledgement);
        WriteOptions(buffer, OpcodeLength, options);

        return buffer;
    }

    /// <summary>The wire token for a mode.</summary>
    public static string ModeToken(TftpTransferMode mode) => mode switch
    {
        TftpTransferMode.NetAscii => NetAsciiToken,
        TftpTransferMode.Octet => OctetToken,
        TftpTransferMode.Mail => MailToken,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "No wire token for this mode."),
    };

    /// <summary>
    /// Reads a mode token. RFC 1350 says the mode is case-insensitive, and devices take it at its
    /// word: "OCTET", "Octet" and "octet" all turn up. An unrecognised token is
    /// <see cref="TftpTransferMode.Unknown"/> rather than an error - the caller keeps the raw
    /// string and can report it.
    /// </summary>
    public static TftpTransferMode ParseMode(string? raw)
    {
        if (string.Equals(raw, OctetToken, StringComparison.OrdinalIgnoreCase))
        {
            return TftpTransferMode.Octet;
        }

        if (string.Equals(raw, NetAsciiToken, StringComparison.OrdinalIgnoreCase))
        {
            return TftpTransferMode.NetAscii;
        }

        if (string.Equals(raw, MailToken, StringComparison.OrdinalIgnoreCase))
        {
            return TftpTransferMode.Mail;
        }

        return TftpTransferMode.Unknown;
    }

    private static bool TryParseRequest(
        ReadOnlySpan<byte> datagram,
        TftpOpcode opcode,
        out TftpMessage? message,
        out string? problem)
    {
        message = null;

        if (!TryReadNulTerminated(datagram, OpcodeLength, out int nameLength, out int afterName))
        {
            problem = "The filename in this request is not null-terminated, so the packet ends in the "
                + "middle of it.";
            return false;
        }

        if (!TryReadNulTerminated(datagram, afterName, out int modeLength, out int afterMode))
        {
            problem = "The transfer mode in this request is missing or not null-terminated.";
            return false;
        }

        if (!TryParseOptions(datagram, afterMode, out TftpOptions options, out problem))
        {
            return false;
        }

        ReadOnlySpan<byte> nameBytes = datagram.Slice(OpcodeLength, nameLength);
        string rawMode = Encoding.Latin1.GetString(datagram.Slice(afterName, modeLength));

        message = new TftpRequestMessage(
            opcode,
            Encoding.Latin1.GetString(nameBytes),
            nameBytes.ToArray(),
            ParseMode(rawMode),
            rawMode,
            options);

        return true;
    }

    private static bool TryParseData(ReadOnlySpan<byte> datagram, out TftpMessage? message, out string? problem)
    {
        message = null;

        if (datagram.Length < DataHeaderLength)
        {
            problem = "A DATA packet needs a two-octet block number after the opcode; this one is "
                + $"{datagram.Length} octets long.";
            return false;
        }

        // A zero-length payload is not malformed. It is how a file whose length is an exact
        // multiple of the block size announces its own end, and rejecting it would turn every
        // exactly-sized file into an unexplained failure.
        message = new TftpDataMessage(
            BinaryPrimitives.ReadUInt16BigEndian(datagram[OpcodeLength..]),
            datagram[DataHeaderLength..].ToArray());

        problem = null;
        return true;
    }

    private static bool TryParseAck(ReadOnlySpan<byte> datagram, out TftpMessage? message, out string? problem)
    {
        message = null;

        if (datagram.Length < DataHeaderLength)
        {
            problem = "An ACK packet needs a two-octet block number after the opcode; this one is "
                + $"{datagram.Length} octets long.";
            return false;
        }

        // Anything past the block number is ignored rather than refused. Some stacks pad short
        // datagrams to a minimum frame size, and a padded ACK is still an ACK.
        message = new TftpAckMessage(BinaryPrimitives.ReadUInt16BigEndian(datagram[OpcodeLength..]));
        problem = null;
        return true;
    }

    private static bool TryParseError(ReadOnlySpan<byte> datagram, out TftpMessage? message, out string? problem)
    {
        message = null;

        if (datagram.Length < DataHeaderLength)
        {
            problem = "An ERROR packet needs a two-octet error code after the opcode; this one is "
                + $"{datagram.Length} octets long.";
            return false;
        }

        var code = (TftpErrorCode)BinaryPrimitives.ReadUInt16BigEndian(datagram[OpcodeLength..]);

        // The message string should be null-terminated and sometimes is not. Taking the rest of
        // the datagram is the lenient reading, and it is the right one: the alternative is
        // discarding the only sentence in the packet that says what went wrong.
        int textLength = TryReadNulTerminated(datagram, DataHeaderLength, out int measured, out _)
            ? measured
            : datagram.Length - DataHeaderLength;

        message = new TftpErrorMessage(
            code,
            Encoding.Latin1.GetString(datagram.Slice(DataHeaderLength, textLength)));

        problem = null;
        return true;
    }

    /// <summary>
    /// Reads the RFC 2347 name/value pairs that follow the fixed part of a request or an OACK.
    /// </summary>
    private static bool TryParseOptions(
        ReadOnlySpan<byte> datagram,
        int start,
        out TftpOptions options,
        out string? problem)
    {
        options = TftpOptions.None;
        problem = null;

        if (start >= datagram.Length)
        {
            return true;
        }

        var parsed = new List<TftpOption>();
        int at = start;

        while (at < datagram.Length)
        {
            if (!TryReadNulTerminated(datagram, at, out int nameLength, out int afterName))
            {
                problem = "An option name at the end of this packet is not null-terminated.";
                return false;
            }

            string name = Encoding.Latin1.GetString(datagram.Slice(at, nameLength));

            if (name.Length == 0)
            {
                // Some stacks pad the tail of a datagram with zeros. A run of them reads as an
                // empty option name, and treating that as the end is kinder than refusing a
                // request that is otherwise perfectly good. Anything else after an empty name is
                // a real malformation and is reported.
                if (IsAllZero(datagram[at..]))
                {
                    break;
                }

                problem = "This packet contains an option with an empty name.";
                return false;
            }

            if (!TryReadNulTerminated(datagram, afterName, out int valueLength, out int afterValue))
            {
                problem = $"Option '{name}' has no value - the packet ends after its name.";
                return false;
            }

            // A header naming one column twice is refused in the CSV importer for the same reason:
            // letting one of them silently win means the tool reports a setting nobody sent.
            foreach (TftpOption existing in parsed)
            {
                if (existing.Is(name))
                {
                    problem = $"Option '{name}' appears more than once in this packet.";
                    return false;
                }
            }

            parsed.Add(new TftpOption(name, Encoding.Latin1.GetString(datagram.Slice(afterName, valueLength))));
            at = afterValue;
        }

        options = parsed.Count == 0 ? TftpOptions.None : new TftpOptions(parsed);
        return true;
    }

    /// <summary>
    /// Finds the NUL that ends a string starting at <paramref name="start"/>.
    /// </summary>
    /// <param name="length">Octets before the NUL. Zero for an empty string.</param>
    /// <param name="next">Index just past the NUL.</param>
    private static bool TryReadNulTerminated(ReadOnlySpan<byte> datagram, int start, out int length, out int next)
    {
        length = 0;
        next = start;

        if (start >= datagram.Length)
        {
            return false;
        }

        int index = datagram[start..].IndexOf((byte)0);
        if (index < 0)
        {
            return false;
        }

        length = index;
        next = start + index + 1;
        return true;
    }

    private static bool IsAllZero(ReadOnlySpan<byte> data)
    {
        foreach (byte value in data)
        {
            if (value != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static int MeasureOptions(TftpOptions options)
    {
        int total = 0;

        foreach (TftpOption option in options.All)
        {
            total += Encoding.Latin1.GetByteCount(option.Name) + 1
                + Encoding.Latin1.GetByteCount(option.Value) + 1;
        }

        return total;
    }

    private static void WriteOptions(byte[] buffer, int at, TftpOptions options)
    {
        foreach (TftpOption option in options.All)
        {
            at = WriteNulTerminated(buffer, at, EncodeString(option.Name, nameof(options)));
            at = WriteNulTerminated(buffer, at, EncodeString(option.Value, nameof(options)));
        }
    }

    private static int WriteNulTerminated(byte[] buffer, int at, byte[] value)
    {
        value.CopyTo(buffer, at);
        buffer[at + value.Length] = 0;
        return at + value.Length + 1;
    }

    /// <summary>
    /// Encodes a string for the wire, refusing an embedded NUL.
    ///
    /// <para>A NUL inside a filename would terminate the field early and produce a packet that
    /// parses cleanly into something nobody asked for. That is a caller mistake rather than a
    /// device fault, so it throws here rather than travelling.</para>
    /// </summary>
    private static byte[] EncodeString(string value, string parameterName)
    {
        if (value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A TFTP string field cannot contain a null character; it would terminate the field early.",
                parameterName);
        }

        return Encoding.Latin1.GetBytes(value);
    }
}
