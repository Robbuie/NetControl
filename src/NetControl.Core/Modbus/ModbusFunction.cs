namespace NetControl.Core.Modbus;

/// <summary>
/// The four Modbus read functions, and nothing else. There is deliberately no write function in this
/// enum: a register write is the one Modbus operation that changes a running machine, and this tool
/// reads to diagnose. Adding one is a decision for the safety rules in CLAUDE.md first.
/// </summary>
public enum ModbusFunction : byte
{
    /// <summary>Function 1: coils (0xxxx), one bit each.</summary>
    ReadCoils = 1,

    /// <summary>Function 2: discrete inputs (1xxxx), one bit each.</summary>
    ReadDiscreteInputs = 2,

    /// <summary>Function 3: holding registers (4xxxx), 16 bits each.</summary>
    ReadHoldingRegisters = 3,

    /// <summary>Function 4: input registers (3xxxx), 16 bits each.</summary>
    ReadInputRegisters = 4,
}
