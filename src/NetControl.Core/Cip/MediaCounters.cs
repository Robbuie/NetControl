using System.Buffers.Binary;
using NetControl.Core.Enip;

namespace NetControl.Core.Cip;

/// <summary>
/// Ethernet Link object (0xF6) attribute 5, Media Counters: twelve UDINTs, little-endian, in the
/// order the spec lists them. These are the ones that explain an intermittent fault - FCS errors
/// are a cable, late collisions are a duplex mismatch - and they are the counters the Rockwell tool
/// never shows anybody.
/// </summary>
public sealed record MediaCounters
{
    /// <summary>Twelve UDINTs.</summary>
    public const int Length = 48;

    public uint AlignmentErrors { get; init; }

    /// <summary>Frames whose checksum failed: the classic damaged-cable counter.</summary>
    public uint FcsErrors { get; init; }

    public uint SingleCollisions { get; init; }

    public uint MultipleCollisions { get; init; }

    public uint SqeTestErrors { get; init; }

    public uint DeferredTransmissions { get; init; }

    /// <summary>A collision after the first 64 bytes. On a switched network, the duplex mismatch signature.</summary>
    public uint LateCollisions { get; init; }

    public uint ExcessiveCollisions { get; init; }

    public uint MacTransmitErrors { get; init; }

    public uint CarrierSenseErrors { get; init; }

    public uint FrameTooLong { get; init; }

    public uint MacReceiveErrors { get; init; }

    /// <summary>Any collision at all. On a full-duplex link there should never be one.</summary>
    public ulong Collisions => (ulong)SingleCollisions + MultipleCollisions + LateCollisions + ExcessiveCollisions;

    public static MediaCounters Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < Length)
        {
            throw new EnipException(
                $"Media counters came back as {bytes.Length} bytes; twelve UDINTs are {Length}.");
        }

        return new MediaCounters
        {
            AlignmentErrors = At(bytes, 0),
            FcsErrors = At(bytes, 1),
            SingleCollisions = At(bytes, 2),
            MultipleCollisions = At(bytes, 3),
            SqeTestErrors = At(bytes, 4),
            DeferredTransmissions = At(bytes, 5),
            LateCollisions = At(bytes, 6),
            ExcessiveCollisions = At(bytes, 7),
            MacTransmitErrors = At(bytes, 8),
            CarrierSenseErrors = At(bytes, 9),
            FrameTooLong = At(bytes, 10),
            MacReceiveErrors = At(bytes, 11),
        };
    }

    private static uint At(ReadOnlySpan<byte> bytes, int index) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes[(index * 4)..]);
}
