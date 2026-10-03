using System.Buffers.Binary;
using System.Net;
using NetControl.App.ViewModels;
using NetControl.Core;
using NetControl.Core.Capture;
using NetControl.Core.Persistence;
using Xunit;

namespace NetControl.Tests;

/// <summary>The Passive tab without a driver says so; with frames, it lists devices - and sends nothing.</summary>
public sealed class PassiveViewModelTests : IDisposable
{
    private readonly ProjectStore _project = ProjectStore.CreateInMemory("Passive");
    private readonly SimulatedDcpNetwork _network = new();

    public void Dispose() => _project.Dispose();

    [Fact]
    public void WithoutNpcapListeningCannotStart()
    {
        _network.Available = false;
        PassiveViewModel viewModel = Build();

        viewModel.CheckAvailability();

        Assert.False(viewModel.StartCommand.CanExecute(null));
        Assert.Contains("Npcap is not installed", viewModel.AvailabilityText, StringComparison.Ordinal);
    }

    [Fact]
    public void FramesBecomeRowsAndNothingIsSent()
    {
        PassiveViewModel viewModel = Build();
        byte[] arp = new byte[42];
        EthernetFrame.Write(arp, MacAddress.Parse("FF:FF:FF:FF:FF:FF"), MacAddress.Parse("00:00:BC:11:22:33"), EthernetFrame.Arp);
        BinaryPrimitives.WriteUInt16BigEndian(arp.AsSpan(14), 1);
        BinaryPrimitives.WriteUInt16BigEndian(arp.AsSpan(16), 0x0800);
        arp[18] = 6;
        arp[19] = 4;
        MacAddress.Parse("00:00:BC:11:22:33").CopyTo(arp.AsSpan(22));
        IPAddress.Parse("192.168.1.51").GetAddressBytes().CopyTo(arp, 28);

        viewModel.Observe(arp, DateTimeOffset.UnixEpoch);
        viewModel.Refresh();

        Assert.Equal(IPAddress.Parse("192.168.1.51"), Assert.Single(viewModel.Devices).Addresses[0]);
        Assert.Empty(_network.Sent);
    }

    private PassiveViewModel Build() =>
        new(new ImmediateDispatcher(), () => _project, () => Nics.Healthy(), _network, new FakeOuiLookup());
}
