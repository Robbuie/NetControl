namespace NetControl.DeviceSim;

/// <summary>
/// One Ethernet Link instance (class 0xF6) - one physical port - and the counters it reports.
/// Mutable, so a test can make a counter move between two reads the way a damaged cable would.
/// </summary>
public sealed class SimulatedLinkPort
{
    /// <summary>Attribute 1, in Mb/s - the spec's unit. 0 with no link.</summary>
    public uint SpeedMbps { get; set; } = 100;

    /// <summary>Attribute 2. Default: link up, full duplex, negotiated (status 3 in bits 2-4).</summary>
    public uint Flags { get; set; } = 0x01 | 0x02 | (3u << 2);

    /// <summary>Attribute 3.</summary>
    public byte[] Mac { get; set; } = [];

    /// <summary>Attribute 4: eleven UDINTs, in the spec's order.</summary>
    public uint[] InterfaceCounters { get; } = new uint[11];

    /// <summary>Attribute 5: twelve UDINTs, in the spec's order. Index 1 is FCS errors, 6 late collisions.</summary>
    public uint[] MediaCounters { get; } = new uint[12];

    public static byte[] Pack(uint[] values)
    {
        var bytes = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), values[i]);
        return bytes;
    }
}
