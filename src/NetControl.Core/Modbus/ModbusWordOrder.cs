namespace NetControl.Core.Modbus;

/// <summary>
/// How a 32-bit value is split across two registers. Modbus never said, so every vendor chose; the
/// results show both so nobody has to guess which one their drive uses.
/// </summary>
public enum ModbusWordOrder
{
    /// <summary>High word first (ABCD) - the more common, and what most PLCs expect.</summary>
    HighFirst,

    /// <summary>Low word first (CDAB) - "word swapped", common on meters and some drives.</summary>
    LowFirst,
}
