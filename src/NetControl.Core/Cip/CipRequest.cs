namespace NetControl.Core.Cip;

/// <summary>
/// One unconnected (UCMM) CIP request: a service code, an EPATH naming what it acts on, and the
/// data for a write.
/// </summary>
public sealed record CipRequest
{
    public required byte Service { get; init; }

    public required ushort Class { get; init; }

    /// <summary>Instance 1 is the only one this tool has ever needed - one adapter, one interface.</summary>
    public ushort Instance { get; init; } = 1;

    /// <summary>Null for a service that acts on the whole object, such as Get_Attribute_All.</summary>
    public ushort? Attribute { get; init; }

    public ReadOnlyMemory<byte> Data { get; init; }

    public static CipRequest GetAttribute(ushort cipClass, ushort attribute, ushort instance = 1) => new()
    {
        Service = CipService.GetAttributeSingle,
        Class = cipClass,
        Instance = instance,
        Attribute = attribute,
    };

    public static CipRequest SetAttribute(
        ushort cipClass, ushort attribute, ReadOnlyMemory<byte> data, ushort instance = 1) => new()
    {
        Service = CipService.SetAttributeSingle,
        Class = cipClass,
        Instance = instance,
        Attribute = attribute,
        Data = data,
    };

    /// <summary>
    /// service byte, EPATH size in 16-bit words, the EPATH, then the data.
    ///
    /// <para>A plain static method rather than something that hands out a span, because the caller
    /// is an async method and <c>Span&lt;T&gt;</c> locals are illegal in one (CS4013). All the
    /// span work in this assembly's protocol code is kept in helpers like this for that reason.</para>
    /// </summary>
    public byte[] Serialize()
    {
        Span<byte> path = stackalloc byte[12];
        int length = 0;

        length += WriteSegment(path[length..], 0x20, 0x21, Class);
        length += WriteSegment(path[length..], 0x24, 0x25, Instance);

        if (Attribute is { } attribute)
        {
            length += WriteSegment(path[length..], 0x30, 0x31, attribute);
        }

        // An EPATH is measured in 16-bit words, so an odd byte count gets a pad.
        if (length % 2 != 0)
        {
            path[length++] = 0;
        }

        var buffer = new byte[2 + length + Data.Length];
        buffer[0] = Service;
        buffer[1] = (byte)(length / 2);
        path[..length].CopyTo(buffer.AsSpan(2));
        Data.Span.CopyTo(buffer.AsSpan(2 + length));
        return buffer;
    }

    /// <summary>
    /// A logical segment, in its 8-bit form where the value fits and its 16-bit form where it does
    /// not. The pad byte before a 16-bit value is required, and leaving it out produces a path the
    /// device rejects with a status that says nothing useful.
    /// </summary>
    private static int WriteSegment(Span<byte> destination, byte byteForm, byte wordForm, ushort value)
    {
        if (value <= 0xFF)
        {
            destination[0] = byteForm;
            destination[1] = (byte)value;
            return 2;
        }

        destination[0] = wordForm;
        destination[1] = 0;
        destination[2] = (byte)(value & 0xFF);
        destination[3] = (byte)(value >> 8);
        return 4;
    }
}
