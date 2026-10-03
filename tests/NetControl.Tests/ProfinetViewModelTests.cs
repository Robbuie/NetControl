using System.Net;
using NetControl.App.ViewModels;
using NetControl.Core;
using NetControl.Core.Persistence;
using NetControl.Core.Profinet;
using Xunit;

namespace NetControl.Tests;

/// <summary>
/// The PROFINET tab's contract: nothing is set without a confirmation, a refused confirmation sends
/// nothing, and only a readback that holds the new value is called verified - all of it recorded.
/// </summary>
public sealed class ProfinetViewModelTests : IDisposable
{
    private static readonly MacAddress Io = MacAddress.Parse("00:1B:1B:AA:BB:01");

    private readonly ProjectStore _project = ProjectStore.CreateInMemory("Profinet");
    private readonly SimulatedDcpNetwork _network = new();

    public ProfinetViewModelTests()
    {
        _network.Devices.Add(new SimulatedDcpNetwork.Device { Mac = Io });
    }

    public void Dispose() => _project.Dispose();

    [Fact]
    public async Task IdentifyListsTheDevicesAndRecordsIt()
    {
        ProfinetViewModel viewModel = Build(confirm: null);

        await viewModel.IdentifyCommand.ExecuteAsync(null);

        Assert.Single(viewModel.Devices);
        Assert.Contains("without a station name", viewModel.Status, StringComparison.Ordinal);
        Assert.Contains(_project.Events.All(), e => e.Category == EventCategory.Profinet && e.Message.StartsWith("DCP Identify", StringComparison.Ordinal));
    }

    /// <summary>No confirmation callback is a refusal, and a refusal sends nothing.</summary>
    [Fact]
    public async Task WithoutAConfirmationNothingIsSent()
    {
        ProfinetViewModel viewModel = await Identified(confirm: null);
        int sentBefore = _network.Sent.Count;

        viewModel.NewName = "io-1";
        await viewModel.SetNameCommand.ExecuteAsync(null);

        Assert.Equal(sentBefore, _network.Sent.Count);
        Assert.Equal(string.Empty, _network.Devices[0].Name);
    }

    [Fact]
    public async Task AConfirmedNameIsSetReadBackAndVerified()
    {
        string? asked = null;
        ProfinetViewModel viewModel = await Identified(confirm: question => { asked = question; return true; });

        viewModel.NewName = "io-1";
        await viewModel.SetNameCommand.ExecuteAsync(null);

        Assert.Contains(Io.ToString(), asked, StringComparison.Ordinal);
        Assert.Equal("io-1", _network.Devices[0].Name);
        Assert.StartsWith("Verified", viewModel.Status, StringComparison.Ordinal);
        Assert.Equal("io-1", viewModel.Selected?.NameOfStation);
        Assert.Contains(_project.Events.All(), e => e.Message.StartsWith("Verified", StringComparison.Ordinal));
    }

    /// <summary>Success on the wire, the old value on the readback: not verified, and said so.</summary>
    [Fact]
    public async Task ADeviceThatSaysYesAndKeepsTheOldValueIsNotVerified()
    {
        _network.Devices[0].Lies = true;
        ProfinetViewModel viewModel = await Identified(confirm: _ => true);

        viewModel.NewIp = "192.168.0.10";
        viewModel.NewMask = "255.255.255.0";
        await viewModel.SetIpCommand.ExecuteAsync(null);

        Assert.NotNull(viewModel.ErrorMessage);
        Assert.Contains("reads back", viewModel.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains(_project.Events.All(), e => e.Severity == EventSeverity.Error);
    }

    [Fact]
    public async Task AnInvalidNameIsSaidBeforeAnyoneIsAsked()
    {
        bool asked = false;
        ProfinetViewModel viewModel = await Identified(confirm: _ => asked = true);

        viewModel.NewName = "Conveyor 1";
        await viewModel.SetNameCommand.ExecuteAsync(null);

        Assert.False(asked);
        Assert.NotNull(viewModel.ErrorMessage);
    }

    [Fact]
    public void WithoutNpcapTheTabSaysWhatToInstall()
    {
        _network.Available = false;
        ProfinetViewModel viewModel = Build(confirm: null);

        viewModel.CheckAvailability();

        Assert.False(viewModel.IsAvailable);
        Assert.Contains("npcap.com", viewModel.Remediation, StringComparison.OrdinalIgnoreCase);
        Assert.False(viewModel.IdentifyCommand.CanExecute(null));
    }

    private ProfinetViewModel Build(Func<string, bool>? confirm)
    {
        var viewModel = new ProfinetViewModel(() => _project, () => Nics.Healthy(), _network)
        {
            IdentifyWindow = TimeSpan.FromMilliseconds(50),
            ConfirmWrite = confirm,
        };

        viewModel.CheckAvailability();
        return viewModel;
    }

    private async Task<ProfinetViewModel> Identified(Func<string, bool>? confirm)
    {
        ProfinetViewModel viewModel = Build(confirm);
        await viewModel.IdentifyCommand.ExecuteAsync(null);
        viewModel.Selected = viewModel.Devices.Single(d => d.Mac == Io);
        return viewModel;
    }
}
