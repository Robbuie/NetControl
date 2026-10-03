using System.Net;
using NetControl.Core.Modbus;
using NetControl.Core.Reachability;
using Xunit;

namespace NetControl.Tests;

/// <summary>
/// The Modbus read, against a server on loopback that remembers every function code it was sent -
/// so "this tool only ever reads" is asserted on the wire rather than claimed in a comment.
/// </summary>
public class ModbusReaderTests
{
    [Fact]
    public async Task ReadsHoldingRegistersBigEndian()
    {
        await using FakeModbusServer server = FakeModbusServer.Start();
        server.Registers[10] = 0x1234;
        server.Registers[11] = 0xFFFE;

        ModbusReadResult result = await new ModbusReader().ReadAsync(Request(server, ModbusFunction.ReadHoldingRegisters, 10, 2));

        Assert.True(result.IsSuccess);
        Assert.Equal(new ushort[] { 0x1234, 0xFFFE }, result.Values);
        Assert.Equal(new List<byte> { 3 }, server.FunctionsSeen);
    }

    [Fact]
    public async Task UnpacksCoilsLeastSignificantBitFirst()
    {
        await using FakeModbusServer server = FakeModbusServer.Start();
        server.Bits[0] = true;
        server.Bits[2] = true;
        server.Bits[9] = true;

        ModbusReadResult result = await new ModbusReader().ReadAsync(Request(server, ModbusFunction.ReadCoils, 0, 10));

        Assert.Equal(new ushort[] { 1, 0, 1, 0, 0, 0, 0, 0, 0, 1 }, result.Values);
    }

    /// <summary>"Illegal data address" is an answer, with advice - not an error.</summary>
    [Fact]
    public async Task AnExceptionIsAResultWithAdvice()
    {
        await using FakeModbusServer server = FakeModbusServer.Start();
        server.AnswerWithException = 2;

        ModbusReadResult result = await new ModbusReader().ReadAsync(Request(server, ModbusFunction.ReadHoldingRegisters, 0, 1));

        Assert.False(result.IsSuccess);
        Assert.Equal((byte)2, result.ExceptionCode);
        Assert.Contains("illegal data address", result.Summary, StringComparison.Ordinal);
        Assert.Contains("one lower", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SilenceAfterConnectingIsAModbusExceptionNamingTheUnitId()
    {
        await using FakeModbusServer server = FakeModbusServer.Start();
        server.Silent = true;

        var reader = new ModbusReader { ResponseTimeout = TimeSpan.FromMilliseconds(300) };

        ModbusException ex = await Assert.ThrowsAsync<ModbusException>(
            () => reader.ReadAsync(Request(server, ModbusFunction.ReadHoldingRegisters, 0, 1)));

        Assert.Contains("unit id", ex.Remediation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefusesABroadcastAndAnOversizedReadBeforeConnecting()
    {
        await Assert.ThrowsAsync<ReachabilityException>(() => new ModbusReader().ReadAsync(new ModbusReadRequest
        {
            Address = IPAddress.Broadcast,
        }));

        await Assert.ThrowsAsync<ArgumentException>(() => new ModbusReader().ReadAsync(new ModbusReadRequest
        {
            Address = IPAddress.Loopback,
            Count = 126,
        }));
    }

    [Fact]
    public void DecodesFloatsBothWordOrdersAndBothNumberings()
    {
        // 123.456f is 0x42F6E979.
        var result = new ModbusReadResult
        {
            Request = new ModbusReadRequest { Address = IPAddress.Loopback, Start = 0, Count = 2 },
            Elapsed = TimeSpan.Zero,
            Values = [0x42F6, 0xE979],
        };

        IReadOnlyList<ModbusValueRow> high = ModbusValueRow.From(result, ModbusWordOrder.HighFirst);
        IReadOnlyList<ModbusValueRow> low = ModbusValueRow.From(result with { Values = [0xE979, 0x42F6] }, ModbusWordOrder.LowFirst);

        Assert.Equal("123.456", high[0].FloatText);
        Assert.Equal("123.456", low[0].FloatText);
        Assert.Null(high[1].FloatText);
        Assert.Equal("40001", high[0].Reference);
        Assert.Equal("0x42F6", high[0].Hex);
        Assert.Equal("-5767", low[0].Signed);
    }

    [Fact]
    public void ReferencesUseSixDigitsPastTheFiveDigitRange()
    {
        Assert.Equal("30001", ModbusValueRow.ReferenceFor(3, 0));
        Assert.Equal("410000", ModbusValueRow.ReferenceFor(4, 9999));
    }

    private static ModbusReadRequest Request(FakeModbusServer server, ModbusFunction function, ushort start, ushort count) => new()
    {
        Address = IPAddress.Loopback,
        Port = server.Port,
        Function = function,
        Start = start,
        Count = count,
    };
}
