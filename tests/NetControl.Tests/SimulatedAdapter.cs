using System.Net;
using System.Net.Sockets;
using NetControl.DeviceSim;
using SimMethod = NetControl.DeviceSim.ConfigMethod;
using SimQuirk = NetControl.DeviceSim.Quirk;

namespace NetControl.Tests;

/// <summary>
/// A fake EtherNet/IP adapter, in this process, on a spare loopback port.
///
/// <para>This is what makes the CIP work testable at a desk. The simulator was written
/// independently of the client and reproduces behaviours seen on real hardware - a device that
/// returns success and discards the write, one whose address is pinned by switches, one that holds
/// a configuration until it is reset. Those are precisely the cases the commissioning sequence
/// exists to handle, and they cannot be produced on demand from a real panel.</para>
///
/// <para>It is not authoritative. It only reproduces quirks somebody has already met and written
/// down, so a green run here means "has not regressed", never "works against that adapter".</para>
/// </summary>
internal sealed class SimulatedAdapter : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop;
    private readonly Task _running;

    private SimulatedAdapter(SimulatedDevice device, int port, CancellationTokenSource stop, Task running)
    {
        Device = device;
        Port = port;
        _stop = stop;
        _running = running;
    }

    public SimulatedDevice Device { get; }

    public int Port { get; }

    /// <summary>Always loopback: a test must never put EtherNet/IP traffic on a real network.</summary>
    public static IPAddress Address => IPAddress.Loopback;

    /// <summary>The address the device currently believes is its own.</summary>
    public IPAddress ConfiguredIp => Device.Ip;

    public IPAddress ConfiguredMask => Device.Mask;

    /// <summary>True once BOOTP/DHCP has actually been turned off on the device.</summary>
    public bool IsStatic => Device.Method == SimMethod.Static;

    /// <summary>True while the device would still ask for an address at its next power cycle.</summary>
    public bool IsStillDynamic => Device.Method != SimMethod.Static;

    public static async Task<SimulatedAdapter> StartAsync(SimQuirk quirks = SimQuirk.None)
    {
        var device = new SimulatedDevice
        {
            Quirks = quirks,

            // Deliberately not loopback: the device starts somewhere else so that a test asserting
            // it ended up at 127.0.0.1 is asserting the write landed, not that nothing happened.
            Ip = IPAddress.Parse("192.168.1.51"),
            Mask = IPAddress.Parse("255.255.255.0"),
            Method = SimMethod.Bootp,
        };

        int port = FreePort();
        var stop = new CancellationTokenSource();
        var server = new EnipServer(device, _ => { }, Address, port);

        var adapter = new SimulatedAdapter(device, port, stop, server.RunAsync(stop.Token));

        await adapter.WaitUntilAcceptingAsync().ConfigureAwait(false);
        return adapter;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);

        try
        {
            await _running.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected: that is how it stops.
        }

        _stop.Dispose();
    }

    /// <summary>
    /// Asks the OS for a port nobody is using. There is a small window between letting it go and
    /// the simulator taking it, which is why <see cref="WaitUntilAcceptingAsync"/> exists rather
    /// than a sleep.
    /// </summary>
    private static int FreePort()
    {
        var probe = new TcpListener(Address, 0);
        probe.Start();

        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    private async Task WaitUntilAcceptingAsync()
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync(Address, Port).ConfigureAwait(false);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(20).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException($"The simulated adapter never started listening on {Address}:{Port}.");
    }
}
