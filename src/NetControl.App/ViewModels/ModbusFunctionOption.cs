using NetControl.Core.Modbus;

namespace NetControl.App.ViewModels;

/// <summary>One entry in the function picker: the function and how a manual names it.</summary>
public sealed record ModbusFunctionOption(ModbusFunction Value, string Label)
{
    public static IReadOnlyList<ModbusFunctionOption> All { get; } =
    [
        new(ModbusFunction.ReadHoldingRegisters, "Holding registers (3, 4xxxx)"),
        new(ModbusFunction.ReadInputRegisters, "Input registers (4, 3xxxx)"),
        new(ModbusFunction.ReadCoils, "Coils (1, 0xxxx)"),
        new(ModbusFunction.ReadDiscreteInputs, "Discrete inputs (2, 1xxxx)"),
    ];

    public override string ToString() => Label;
}
