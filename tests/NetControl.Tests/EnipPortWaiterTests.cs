using System.Net;
using System.Net.Sockets;
using NetControl.Core.Commissioning;
using Xunit;

namespace NetControl.Tests;

/// <summary>
/// The wait between "served" and Set static: a device that has just taken an address needs a few
/// seconds before it answers on TCP/44818. Loopback only.
/// </summary>
public class EnipPortWaiterTests
{
    [Fact]
    public async Task ReturnsAsSoonAsThePortAccepts()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            Assert.True(await EnipPortWaiter.WaitAsync(IPAddress.Loopback, port, TimeSpan.FromSeconds(5)));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task KeepsTryingUntilTheDeviceComesUp()
    {
        int port = FreePort();
        var listener = new TcpListener(IPAddress.Loopback, port);

        Task<bool> waiting = EnipPortWaiter.WaitAsync(
            IPAddress.Loopback, port, TimeSpan.FromSeconds(10), pause: TimeSpan.FromMilliseconds(50));

        await Task.Delay(300);
        Assert.False(waiting.IsCompleted);

        listener.Start();
        try
        {
            Assert.True(await waiting);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task GivesUpWhenNothingEverAnswers()
    {
        int port = FreePort();

        Assert.False(await EnipPortWaiter.WaitAsync(
            IPAddress.Loopback, port, TimeSpan.FromMilliseconds(400), pause: TimeSpan.FromMilliseconds(50)));
    }

    [Fact]
    public async Task RefusesABroadcast()
    {
        await Assert.ThrowsAsync<NetControl.Core.Reachability.ReachabilityException>(() =>
            EnipPortWaiter.WaitAsync(IPAddress.Broadcast, 44818, TimeSpan.FromSeconds(1)));
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
