using System.Buffers.Binary;
using NetControl.Core.Enip;

namespace NetControl.Core.Cip;

/// <summary>
/// Ethernet Link object (0xF6) attribute 4, Interface Counters: eleven UDINTs, little-endian, in
/// the order the spec lists them. Counted since power-up or since somebody last cleared them - this
/// tool never clears them, because clearing is a write and it destroys the history the next person
/// to look will need.
/// </summary>
public sealed record InterfaceCounters
{
    /// <summary>Eleven UDINTs.</summary>
    public const int Length = 44;

    public uint InOctets { get; init; }

    public uint InUnicastPackets { get; init; }

    public uint InNonUnicastPackets { get; init; }

    /// <summary>Frames received and thrown away for want of room - the device could not keep up.</summary>
    public uint InDiscards { get; init; }

    public uint InErrors { get; init; }

    public uint InUnknownProtocols { get; init; }

    public uint OutOctets { get; init; }

    public uint OutUnicastPackets { get; init; }

    public uint OutNonUnicastPackets { get; init; }

    public uint OutDiscards { get; init; }

    public uint OutErrors { get; init; }

    public static InterfaceCounters Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < Length)
        {
            throw new EnipException(
                $"Interface counters came back as {bytes.Length} bytes; eleven UDINTs are {Length}.");
        }

        return new InterfaceCounters
        {
            InOctets = At(bytes, 0),
            InUnicastPackets = At(bytes, 1),
            InNonUnicastPackets = At(bytes, 2),
            InDiscards = At(bytes, 3),
            InErrors = At(bytes, 4),
            InUnknownProtocols = At(bytes, 5),
            OutOctets = At(bytes, 6),
            OutUnicastPackets = At(bytes, 7),
            OutNonUnicastPackets = At(bytes, 8),
            OutDiscards = At(bytes, 9),
            OutErrors = At(bytes, 10),
        };
    }

    private static uint At(ReadOnlySpan<byte> bytes, int index) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes[(index * 4)..]);
}
