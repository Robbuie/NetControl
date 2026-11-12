using NetControl.Core.Dhcp;
using NetControl.Core.Safety;
using Xunit;

namespace NetControl.Tests;

/// <summary>
/// The server against a device that will not stop asking, and in listen-only mode. Real sockets on
/// loopback, as in <see cref="DhcpServerWireTests"/>; nothing is broadcast.
/// </summary>
public sealed class DhcpServerSafetyTests
{
    private static byte[] BootpRequest => Frames.Bytes(Frames.BootpRequest);

    [Fact]
    public async Task ListenOnlyModeSendsNoReplyEvenToAPlannedDeviceAndSaysWhy()
    {
        var guard = new NetworkGuard { ListenOnly = true };
        BootpPacket.TryParse(BootpRequest, out BootpPacket? parsed);
        var planned = new DeviceAssignment(
            parsed!.ClientMac,
            System.Net.IPAddress.Parse("192.168.1.51"),
            System.Net.IPAddress.Parse("255.255.255.0"));

        await using WireHarness harness = await WireHarness.StartAsync(
            new StaticMapPolicy([planned]),
            DhcpServerMode.Serve,
            listenAsDevice: true,
            guard: guard);

        harness.Send(BootpRequest);
        DhcpRequestEventArgs request = await harness.NextRequestAsync();

        Assert.True(request.Decision.ShouldServe);
        Assert.True(await harness.StayedSilentAsync(TimeSpan.FromMilliseconds(500)));
        Assert.Contains(harness.Faults(), f => f.Message.Contains("Listen-only", StringComparison.Ordinal));
        Assert.Equal(1, guard.Ledger.TotalBlocked);
    }

    [Fact]
    public async Task ADeviceAskingHundredsOfTimesASecondIsHandledOnlyUpToItsAllowance()
    {
        await using WireHarness harness = await WireHarness.StartAsync(new StaticMapPolicy());
        byte[] request = BootpRequest;

        for (int i = 0; i < 200; i++)
        {
            // A fresh transaction id each time, so none of them is a retransmit.
            request[4] = (byte)i;
            request[5] = (byte)(i >> 8);
            harness.Send(request);
        }

        int handled = 0;
        try
        {
            while (true)
            {
                await harness.NextRequestAsync(TimeSpan.FromMilliseconds(400));
                handled++;
            }
        }
        catch (TimeoutException)
        {
            // Drained.
        }

        // Up to the allowance, for each one-second window the burst happened to straddle.
        Assert.InRange(handled, 1, 2 * harness.Options.MaxRequestsPerClientPerSecond);
    }
}
