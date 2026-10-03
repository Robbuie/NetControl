using System.Net;

namespace NetControl.Core.Modbus;

/// <summary>
/// One read: which device, which unit behind it, which table, from where, and how many.
///
/// <para><see cref="Start"/> is the zero-based address on the wire. Manuals mix that with the
/// one-based "Modicon" reference (40001 is holding register 0), and getting the two confused is the
/// most common reason a Modbus read returns the neighbour of the value somebody wanted - so the
/// results show both.</para>
/// </summary>
public sealed record ModbusReadRequest
{
    /// <summary>Registers per read, from the spec: 125 x 2 bytes fits the 253-byte PDU.</summary>
    public const int MaxRegisters = 125;

    /// <summary>Bits per read, from the spec.</summary>
    public const int MaxBits = 2000;

    public required IPAddress Address { get; init; }

    public int Port { get; init; } = 502;

    /// <summary>
    /// The unit identifier. 1 is the usual answer for a device on Ethernet; behind a gateway it is
    /// the serial slave address. 255 asks the gateway itself.
    /// </summary>
    public byte UnitId { get; init; } = 1;

    public ModbusFunction Function { get; init; } = ModbusFunction.ReadHoldingRegisters;

    public ushort Start { get; init; }

    public ushort Count { get; init; } = 10;

    public bool IsBits => Function is ModbusFunction.ReadCoils or ModbusFunction.ReadDiscreteInputs;

    /// <summary>The Modicon table prefix: 0, 1, 3 or 4.</summary>
    public int TablePrefix => Function switch
    {
        ModbusFunction.ReadCoils => 0,
        ModbusFunction.ReadDiscreteInputs => 1,
        ModbusFunction.ReadInputRegisters => 3,
        _ => 4,
    };

    /// <summary>What is wrong with the request, or null. Checked before anything is sent.</summary>
    public string? Problem()
    {
        int max = IsBits ? MaxBits : MaxRegisters;

        if (Count == 0)
        {
            return "Count must be at least 1.";
        }

        if (Count > max)
        {
            return $"At most {max} {(IsBits ? "bits" : "registers")} can be read at once - that is the protocol's limit, not this tool's.";
        }

        if (Start + Count > 65536)
        {
            return $"Start {Start} plus count {Count} runs past the last address, 65535.";
        }

        return Port is < 1 or > 65535 ? $"Port {Port} is not a TCP port." : null;
    }

    public string Describe() =>
        $"{Function} unit {UnitId}, {Count} from {Start} at {Address}" + (Port == 502 ? string.Empty : $":{Port}");
}
