using System.Buffers.Binary;

namespace NetControl.Core.Modbus;

/// <summary>
/// The Modbus/TCP read request and response, as bytes. Big-endian throughout - the MBAP header and
/// every register - which is the one thing in this codebase that is uniformly so.
/// </summary>
internal static class ModbusFrame
{
    public const int MbapLength = 7;

    public static byte[] BuildRead(ushort transactionId, ModbusReadRequest request)
    {
        var frame = new byte[MbapLength + 5];
        BinaryPrimitives.WriteUInt16BigEndian(frame, transactionId);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), 6);   // unit + function + start + count
        frame[6] = request.UnitId;
        frame[7] = (byte)request.Function;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(8), request.Start);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(10), request.Count);
        return frame;
    }

    /// <summary>The whole frame's length once the header is in, or null before it is.</summary>
    public static int? ExpectedLength(ReadOnlySpan<byte> received) =>
        received.Length < 6 ? null : 6 + BinaryPrimitives.ReadUInt16BigEndian(received[4..]);

    /// <summary>
    /// Reads a response to <paramref name="request"/>. Registers come back as 16-bit words; bits are
    /// unpacked LSB first, the way the spec packs them, and returned as 0 and 1 in the same array so
    /// the two tables can share one result type.
    /// </summary>
    public static ModbusReadResult Parse(ReadOnlySpan<byte> frame, ushort transactionId, ModbusReadRequest request, TimeSpan elapsed)
    {
        if (frame.Length < MbapLength + 2)
        {
            return ModbusReadResult.Malformed(request, elapsed, $"the reply was {frame.Length} bytes - too short to be Modbus/TCP");
        }

        if (BinaryPrimitives.ReadUInt16BigEndian(frame) != transactionId)
        {
            return ModbusReadResult.Malformed(request, elapsed, "the reply carried a different transaction id - it answered somebody else's question");
        }

        if (BinaryPrimitives.ReadUInt16BigEndian(frame[2..]) != 0)
        {
            return ModbusReadResult.Malformed(request, elapsed, "the reply's protocol id is not Modbus");
        }

        byte function = frame[7];

        if (function == ((byte)request.Function | 0x80))
        {
            return ModbusReadResult.FromException(request, elapsed, frame[8]);
        }

        if (function != (byte)request.Function)
        {
            return ModbusReadResult.Malformed(request, elapsed, $"the reply is for function {function}, not {(byte)request.Function}");
        }

        int byteCount = frame[8];
        ReadOnlySpan<byte> data = frame[(MbapLength + 2)..];

        if (data.Length < byteCount)
        {
            return ModbusReadResult.Malformed(request, elapsed, $"the reply says {byteCount} data bytes and carries {data.Length}");
        }

        var values = new ushort[request.Count];

        if (request.IsBits)
        {
            if (byteCount < (request.Count + 7) / 8)
            {
                return ModbusReadResult.Malformed(request, elapsed, $"{byteCount} bytes cannot hold {request.Count} bits");
            }

            for (int i = 0; i < request.Count; i++)
            {
                values[i] = (ushort)((data[i / 8] >> (i % 8)) & 1);
            }
        }
        else
        {
            if (byteCount != request.Count * 2)
            {
                return ModbusReadResult.Malformed(request, elapsed, $"asked for {request.Count} registers and got {byteCount / 2}");
            }

            for (int i = 0; i < request.Count; i++)
            {
                values[i] = BinaryPrimitives.ReadUInt16BigEndian(data[(i * 2)..]);
            }
        }

        return ModbusReadResult.Read(request, elapsed, values);
    }
}
