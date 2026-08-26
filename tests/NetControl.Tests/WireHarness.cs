using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using NetControl.Core.Dhcp;
using NetControl.Core.Interfaces;

namespace NetControl.Tests;

/// <summary>
/// Runs a real <see cref="DhcpServer"/> on a real socket and gives a test somewhere to send
/// bytes from and somewhere to watch for a reply.
///
/// Ports are picked from the ephemeral range rather than 67/68: the real ports are frequently
/// already owned on a developer machine (the Hyper-V Default Switch alone takes one), and a test
/// run must never fight the operating system for them.
/// </summary>
internal sealed class WireHarness : IAsyncDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private readonly CancellationTokenSource _cts = new();
    private readonly Socket _client = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    private readonly Socket? _device;
    private readonly Channel<DhcpRequestEventArgs> _requests = Channel.CreateUnbounded<DhcpRequestEventArgs>();
    private readonly Channel<DhcpReplyEventArgs> _replies = Channel.CreateUnbounded<DhcpReplyEventArgs>();
    private readonly Channel<DhcpFaultEventArgs> _faults = Channel.CreateUnbounded<DhcpFaultEventArgs>();

    private Task _run = Task.CompletedTask;

    private WireHarness(DhcpServer server, FakeNicInventory nics, DhcpServerOptions options, bool listenAsDevice)
    {
        Server = server;
        Inventory = nics;
        Options = options;

        if (listenAsDevice)
        {
            _device = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _device.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _device.EnableBroadcast = true;
            _device.Bind(new IPEndPoint(IPAddress.Any, options.ClientPort));
        }
    }

    public DhcpServer Server { get; }

    public FakeNicInventory Inventory { get; }

    public DhcpServerOptions Options { get; }

    public static async Task<WireHarness> StartAsync(
        IAssignmentPolicy policy,
        DhcpServerMode mode = DhcpServerMode.Watch,
        NicInfo? nic = null,
        int? interfaceIndexFilter = null,
        ReplySendMode sendMode = ReplySendMode.UnicastInterfaceOption,
        bool listenAsDevice = false)
    {
        var options = new DhcpServerOptions
        {
            ListenPort = FreeUdpPort(),
            ClientPort = FreeUdpPort(),
            Mode = mode,
            SendMode = sendMode,
            InterfaceIndexFilter = interfaceIndexFilter,
        };

        var nics = new FakeNicInventory(nic ?? Nics.Healthy());
        var server = new DhcpServer(nics, policy, options);
        var harness = new WireHarness(server, nics, options, listenAsDevice);
        await harness.RunAsync().ConfigureAwait(false);
        return harness;
    }

    /// <summary>Puts raw bytes on the wire, exactly as a device would.</summary>
    public void Send(byte[] datagram) =>
        _client.SendTo(datagram, new IPEndPoint(IPAddress.Loopback, Options.ListenPort));

    public async Task<DhcpRequestEventArgs> NextRequestAsync(TimeSpan? timeout = null) =>
        await _requests.Reader.ReadAsync().AsTask().WaitAsync(timeout ?? DefaultTimeout).ConfigureAwait(false);

    public async Task<DhcpReplyEventArgs> NextReplyAsync(TimeSpan? timeout = null) =>
        await _replies.Reader.ReadAsync().AsTask().WaitAsync(timeout ?? DefaultTimeout).ConfigureAwait(false);

    public IReadOnlyList<DhcpFaultEventArgs> Faults()
    {
        var list = new List<DhcpFaultEventArgs>();
        while (_faults.Reader.TryRead(out DhcpFaultEventArgs? fault))
        {
            list.Add(fault);
        }

        return list;
    }

    /// <summary>
    /// True if the engine recorded no reply AND nothing landed on the client port within the
    /// window. Both halves matter: the first says the engine believes it stayed quiet, the second
    /// says it actually did.
    /// </summary>
    public async Task<bool> StayedSilentAsync(TimeSpan window)
    {
        Task<byte[]?> onTheWire = _device is null
            ? Task.FromResult<byte[]?>(null)
            : ReceiveOnceAsync(_device, window);

        bool engineReportedAReply;
        try
        {
            await _replies.Reader.ReadAsync().AsTask().WaitAsync(window).ConfigureAwait(false);
            engineReportedAReply = true;
        }
        catch (TimeoutException)
        {
            engineReportedAReply = false;
        }

        byte[]? received = await onTheWire.ConfigureAwait(false);
        return !engineReportedAReply && received is null;
    }

    /// <summary>The bytes the device actually saw, or null if nothing arrived in time.</summary>
    public Task<byte[]?> ReceiveAsDeviceAsync(TimeSpan? timeout = null) =>
        _device is null
            ? throw new InvalidOperationException("This harness was not started with listenAsDevice: true.")
            : ReceiveOnceAsync(_device, timeout ?? DefaultTimeout);

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);

        try
        {
            await _run.WaitAsync(DefaultTimeout).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected: cancellation is how the loop is meant to end.
        }
        catch (TimeoutException)
        {
            // Worth knowing about, but a hung shutdown must not mask the assertion that failed.
        }

        _client.Dispose();
        _device?.Dispose();
        _cts.Dispose();
    }

    private static int FreeUdpPort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }

    private static async Task<byte[]?> ReceiveOnceAsync(Socket socket, TimeSpan timeout)
    {
        var buffer = new byte[2048];
        using var cts = new CancellationTokenSource(timeout);

        try
        {
            SocketReceiveFromResult result = await socket
                .ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), cts.Token)
                .ConfigureAwait(false);

            return buffer[..result.ReceivedBytes];
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private async Task RunAsync()
    {
        var listening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Server.Listening += (_, _) => listening.TrySetResult();
        Server.RequestReceived += (_, e) => _requests.Writer.TryWrite(e);
        Server.ReplySent += (_, e) => _replies.Writer.TryWrite(e);
        Server.Fault += (_, e) => _faults.Writer.TryWrite(e);

        _run = Task.Run(() => Server.RunAsync(_cts.Token));

        Task first = await Task.WhenAny(listening.Task, _run).WaitAsync(DefaultTimeout).ConfigureAwait(false);

        if (first == _run)
        {
            // Surfaces the bind failure rather than letting the test time out on a silent socket.
            await _run.ConfigureAwait(false);
            throw new InvalidOperationException("The server stopped before it started listening.");
        }
    }
}
