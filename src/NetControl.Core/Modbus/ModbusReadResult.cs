using NetControl.Core.Reachability;

namespace NetControl.Core.Modbus;

/// <summary>
/// How a read went. Three shapes: values, a Modbus exception (the device understood and said no),
/// or a reply that was not a sensible answer. An exception is a perfectly good answer - "illegal
/// data address" tells you the register map you are reading from is wrong - so it is a result,
/// not an error.
/// </summary>
public sealed record ModbusReadResult
{
    public required ModbusReadRequest Request { get; init; }

    public required TimeSpan Elapsed { get; init; }

    /// <summary>The values, one per address asked for. Bits as 0 and 1. Empty unless <see cref="IsSuccess"/>.</summary>
    public IReadOnlyList<ushort> Values { get; init; } = [];

    /// <summary>The Modbus exception code, when the device answered with one.</summary>
    public byte? ExceptionCode { get; init; }

    /// <summary>Why the reply could not be read, when it could not.</summary>
    public string? Malformation { get; init; }

    public bool IsSuccess => ExceptionCode is null && Malformation is null;

    public string Summary =>
        IsSuccess
            ? $"Read {Values.Count} {(Request.IsBits ? "bit(s)" : "register(s)")} from {Request.Address} unit {Request.UnitId} in {Elapsed.TotalMilliseconds:0} ms."
            : ExceptionCode is byte code
                ? $"{Request.Address} unit {Request.UnitId} answered with exception {code}: {ModbusHandshake.ModbusExceptionText(code)}. {Advice(code)}"
                : $"{Request.Address} answered, but {Malformation}.";

    internal static ModbusReadResult Read(ModbusReadRequest request, TimeSpan elapsed, IReadOnlyList<ushort> values) =>
        new() { Request = request, Elapsed = elapsed, Values = values };

    internal static ModbusReadResult FromException(ModbusReadRequest request, TimeSpan elapsed, byte code) =>
        new() { Request = request, Elapsed = elapsed, ExceptionCode = code };

    internal static ModbusReadResult Malformed(ModbusReadRequest request, TimeSpan elapsed, string why) =>
        new() { Request = request, Elapsed = elapsed, Malformation = why };

    /// <summary>What to try next for the exceptions people actually meet.</summary>
    private static string Advice(byte code) => code switch
    {
        1 => "The device does not support this function - try the other register table.",
        2 => "Nothing is mapped at that address. Check whether the manual counts from 0 or from 1, and try one lower.",
        3 => "The count is wrong for this device - many accept fewer registers per read than the protocol allows.",
        10 or 11 => "A gateway answered: the unit id is the serial address of the device behind it, and that device did not reply.",
        _ => string.Empty,
    };
}
