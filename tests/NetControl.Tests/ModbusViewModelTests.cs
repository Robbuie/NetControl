using System.Globalization;
using NetControl.App.ViewModels;
using NetControl.Core.Persistence;
using Xunit;

namespace NetControl.Tests;

/// <summary>The Modbus tab: a read lands in the grid and in the record; a bad box is said, not sent.</summary>
public sealed class ModbusViewModelTests : IDisposable
{
    private readonly ProjectStore _project = ProjectStore.CreateInMemory("Modbus");

    public void Dispose() => _project.Dispose();

    [Fact]
    public async Task AReadFillsTheGridAndIsRecorded()
    {
        await using FakeModbusServer server = FakeModbusServer.Start();
        server.Registers[0] = 42;

        var viewModel = new ModbusViewModel(() => _project)
        {
            TargetText = "127.0.0.1",
            PortText = server.Port.ToString(CultureInfo.InvariantCulture),
            CountText = "2",
        };

        await viewModel.ReadCommand.ExecuteAsync(null);

        Assert.Null(viewModel.ErrorMessage);
        Assert.Equal(2, viewModel.Rows.Count);
        Assert.Equal("42", viewModel.Rows[0].Unsigned);

        EventRecord row = Assert.Single(_project.Events.All());
        Assert.Equal(EventCategory.Modbus, row.Category);
        Assert.Equal("127.0.0.1", row.Target);
    }

    [Fact]
    public async Task ABadCountIsSaidAndNothingIsSent()
    {
        var viewModel = new ModbusViewModel(() => _project)
        {
            TargetText = "127.0.0.1",
            CountText = "500",
        };

        await viewModel.ReadCommand.ExecuteAsync(null);

        Assert.Contains("125", viewModel.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(_project.Events.All());
    }
}
