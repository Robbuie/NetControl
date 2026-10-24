using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using NetControl.App.Serving;
using NetControl.App.ViewModels;
using NetControl.Core;
using NetControl.Core.Commissioning;
using NetControl.Core.Dhcp;
using NetControl.Core.Interfaces;
using NetControl.Core.Oui;
using NetControl.Core.Persistence;
using Xunit;

namespace NetControl.Tests;

/// <summary>
/// Assign IP, end to end on loopback: a device asks, it is planned, served by the real listener on a
/// real socket, and BOOTP/DHCP is turned off on a simulated adapter over real TCP - the Rockwell
/// tool's two buttons as one.
///
/// <para>Hermetic. The request carries a relay address of 127.0.0.1, so the reply is unicast back
/// to loopback rather than broadcast on whatever network the machine is plugged into, and the
/// listener is on a spare port rather than 67.</para>
/// </summary>
public sealed class MainViewModelAssignTests : IAsyncDisposable
{
    private static readonly MacAddress Device = MacAddress.Parse("00:00:BC:5E:11:01");

    private readonly ProjectStore _project = ProjectStore.CreateInMemory("Test");
    private readonly StaticMapPolicy _policy = new();
    private readonly FakeNicInventory _nics = new(Loopback());
    private readonly ServerController _controller;
    private readonly MainViewModel _viewModel;
    private readonly int _listenPort = FreeUdpPort();

    public MainViewModelAssignTests()
    {
        _controller = new ServerController(_nics, _policy);
        _viewModel = new MainViewModel(
            new ImmediateDispatcher(),
            _nics,
            new StubPreflight(),
            _controller,
            _policy,
            OuiDatabase.Empty,
            _project,
            listenPort: _listenPort);

        _viewModel.InterfaceBar.ApplyInventory([Loopback()]);
        _viewModel.InterfaceBar.SelectedAdapter = _viewModel.InterfaceBar.Adapters[0];
    }

    [Fact]
    public async Task ServesTheDeviceThenTurnsBootpOffAndVerifies()
    {
        await using SimulatedAdapter device = await SimulatedAdapter.StartAsync();
        _viewModel.Plan.CommissionPort = device.Port;

        Task assigning = _viewModel.AssignAsync(Device, new AddressAssignment
        {
            Ip = IPAddress.Loopback,
            Mask = IPAddress.Parse("255.0.0.0"),
            MakeStatic = true,
        });

        await AskUntilAsync(assigning);
        await assigning.WaitAsync(TimeSpan.FromSeconds(30));

        DeviceRowViewModel row = Assert.Single(_viewModel.Plan.Rows);
        Assert.Equal(Device, row.Mac);
        Assert.Equal(DeviceState.Verified, row.State);
        Assert.True(device.IsStatic);
        Assert.Equal(IPAddress.Loopback, device.ConfiguredIp);

        // Serve was only started for this device, so it is not left running behind the user's back.
        Assert.Equal(ServerRunState.Stopped, _viewModel.RunState);
        Assert.False(_viewModel.IsServeArmed);
        Assert.False(_viewModel.IsAssigning);
        Assert.Null(_viewModel.ErrorMessage);
    }

    [Fact]
    public async Task ServeOnlyLeavesTheDeviceDynamicAndTheListenerServing()
    {
        await using SimulatedAdapter device = await SimulatedAdapter.StartAsync();
        _viewModel.Plan.CommissionPort = device.Port;

        Task assigning = _viewModel.AssignAsync(Device, new AddressAssignment
        {
            Ip = IPAddress.Loopback,
            Mask = IPAddress.Parse("255.0.0.0"),
            MakeStatic = false,
        });

        await AskUntilAsync(assigning);
        await assigning.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(DeviceState.Served, Assert.Single(_viewModel.Plan.Rows).State);
        Assert.True(device.IsStillDynamic);
        Assert.Equal(ServerRunState.Listening, _viewModel.RunState);
    }

    [Fact]
    public async Task PressingStopWhileWaitingCancelsAndSendsNothingToTheDevice()
    {
        await using SimulatedAdapter device = await SimulatedAdapter.StartAsync();
        _viewModel.Plan.CommissionPort = device.Port;

        Task assigning = _viewModel.AssignAsync(Device, new AddressAssignment
        {
            Ip = IPAddress.Loopback,
            Mask = IPAddress.Parse("255.0.0.0"),
            MakeStatic = true,
        });

        await WaitForAsync(() => _viewModel.RunState == ServerRunState.Listening);
        await _viewModel.StopCommand.ExecuteAsync(null);
        await assigning.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains("cancelled", _viewModel.ErrorMessage, StringComparison.Ordinal);
        Assert.True(device.IsStillDynamic);
        Assert.False(_viewModel.IsAssigning);
    }

    [Fact]
    public void TheDialogStartsFromTheAdaptersMaskAndGateway()
    {
        NicInfo withGateway = Loopback() with { Gateway = IPAddress.Parse("127.0.0.254") };
        _viewModel.InterfaceBar.ApplyInventory([withGateway]);
        _viewModel.InterfaceBar.SelectedAdapter = _viewModel.InterfaceBar.Adapters[0];

        AssignDefaults? defaults = _viewModel.AssignDefaultsFor(
            LogEntryViewModel.FromRequest(
                Events.Request(Device, AssignmentDecision.Ignore("not in the plan")),
                vendor: null,
                planned: null,
                DhcpServerMode.Watch,
                selectedInterfaceIndex: null));

        Assert.NotNull(defaults);
        Assert.Equal(string.Empty, defaults.Ip);
        Assert.Equal("255.0.0.0", defaults.Mask);
        Assert.Equal("127.0.0.254", defaults.Gateway);
    }

    public async ValueTask DisposeAsync()
    {
        await _controller.StopAsync();
        _viewModel.Dispose();
        _controller.Dispose();
        _project.Dispose();
    }

    /// <summary>The device asking, every 200 ms, the way a real one retransmits.</summary>
    private async Task AskUntilAsync(Task assigning)
    {
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        uint xid = 0x5EED0001;

        for (int i = 0; i < 100 && !assigning.IsCompleted; i++)
        {
            if (_viewModel.RunState == ServerRunState.Listening)
            {
                var request = new BootpPacket
                {
                    Op = BootpPacket.OpBootRequest,
                    TransactionId = xid++,
                    ClientMac = Device,

                    // Relayed, so the reply comes back to loopback rather than out as a broadcast.
                    GatewayIp = IPAddress.Loopback,
                };

                client.SendTo(request.Serialize(), new IPEndPoint(IPAddress.Loopback, _listenPort));
            }

            if (_viewModel.Plan.Rows.Any(r => r.State >= DeviceState.Served))
            {
                return;
            }

            await Task.Delay(200);
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (int i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(50);
        }

        Assert.True(condition());
    }

    private static NicInfo Loopback() => new(
        NetworkInterface.LoopbackInterfaceIndex,
        "Loopback",
        "Loopback",
        MacAddress.Parse("02:00:00:00:00:01"),
        IPAddress.Loopback,
        IPAddress.Parse("255.0.0.0"),
        OperationalStatus.Up,
        NetworkInterfaceType.Ethernet,
        1_000_000_000);

    private static int FreeUdpPort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }
}
