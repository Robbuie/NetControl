using System.Buffers.Binary;

namespace NetControl.Core.Capture;

/// <summary>
/// The Ethernet II header, with one 802.1Q tag allowed. Every raw frame parser in the tool starts
/// here, so a VLAN-tagged frame is understood everywhere or nowhere.
/// </summary>
public readonly record struct EthernetFrame(MacAddress Destination, MacAddress Source, ushort EtherType, int PayloadOffset, ushort? Vlan)
{
    public const int HeaderLength = 14;

    public const ushort Arp = 0x0806;
    public const ushort IPv4 = 0x0800;
    public const ushort Vlan8021Q = 0x8100;
    public const ushort Lldp = 0x88CC;
    public const ushort Profinet = 0x8892;

    /// <summary>
    /// Reads the header, or false for a frame too short to have one, or an 802.3 length-field frame
    /// (LLC), which carries nothing this tool reads.
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> frame, out EthernetFrame header)
    {
        header = default;

        if (frame.Length < HeaderLength)
        {
            return false;
        }

        ushort type = BinaryPrimitives.ReadUInt16BigEndian(frame[12..]);
        int offset = HeaderLength;
        ushort? vlan = null;

        if (type == Vlan8021Q)
        {
            if (frame.Length < HeaderLength + 4)
            {
                return false;
            }

            vlan = (ushort)(BinaryPrimitives.ReadUInt16BigEndian(frame[14..]) & 0x0FFF);
            type = BinaryPrimitives.ReadUInt16BigEndian(frame[16..]);
            offset += 4;
        }

        if (type < 0x0600)
        {
            return false;
        }

        header = new EthernetFrame(new MacAddress(frame), new MacAddress(frame[6..]), type, offset, vlan);
        return true;
    }

    /// <summary>A header for a frame this tool sends: untagged.</summary>
    public static void Write(Span<byte> frame, MacAddress destination, MacAddress source, ushort etherType)
    {
        destination.CopyTo(frame);
        source.CopyTo(frame[6..]);
        BinaryPrimitives.WriteUInt16BigEndian(frame[12..], etherType);
    }
}
