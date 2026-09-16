using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using NetControl.Core.Interfaces;
using NetControl.Core.Tftp;

namespace NetControl.Tests;

/// <summary>
/// Runs a real <see cref="TftpWatchServer"/> on a real socket, and gives a test somewhere to send
/// bytes from and somewhere to watch for the refusal.
///
/// <para>The sibling of <see cref="WireHarness"/>, and it picks its port the same way and for the
/// same reason: UDP/69 is frequently already owned on a machine that has a TFTP server installed,
/// which is exactly the machine somebody runs these tests on, and a test run must never fight the
/// operating system - or the plant's backup server - for a port.</para>
///
/// <para>The client socket both sends and receives, because in TFTP those are the same socket: the
/// port a request leaves from is the client's transfer identifier and the reply is addressed back
/// to it.</para>
/// </summary>
internal sealed class TftpWireHarness : IAsyncDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private readonly CancellationTokenSource _cts = new();
    private readonly Socket _client = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    private readonly Channel<TftpRequestEventArgs> _requests = Channel.CreateUnbounded<TftpRequestEventArgs>();
    private readonly Channel<TftpFaultEventArgs> _faults = Channel.CreateUnbounded<TftpFaultEventArgs>();

    private Task _run = Task.CompletedTask;

    private TftpWireHarness(TftpWatchServer server, FakeNicInventory nics, TftpWatchOptions options)
    {
        Server = server;
        Inventory = nics;
        Options = options;
    }

    public TftpWatchServer Server { get; }

    public FakeNicInventory Inventory { get; }

    public TftpWatchOptions Options { get; }

    /// <summary>The port this harness's client sends from - the client's TID.</summary>
    public int ClientPort => ((IPEndPoint)_client.LocalEndPoint!).Port;

    public static async Task<TftpWireHarness> StartAsync(
        int? interfaceIndexFilter = null,
        bool sendRefusal = true,
        NicInfo? nic = null,
        TftpRefusalSource refusalSource = TftpRefusalSource.ListeningSocket)
    {
        var options = new TftpWatchOptions
        {
            ListenPort = FreeUdpPort(),
            InterfaceIndexFilter = interfaceIndexFilter,
            SendRefusal = sendRefusal,
            RefusalSource = refusalSource,
        };

        var nics = new FakeNicInventory(nic ?? Nics.Healthy());
        var server = new TftpWatchServer(nics, options);
        var harness = new TftpWireHarness(server, nics, options);
        await harness.RunAsync().ConfigureAwait(false);
        return harness;
    }

    /// <summary>Puts raw bytes on the wire, exactly as a controller would.</summary>
    public void Send(byte[] datagram) =>
        _client.SendTo(datagram, new IPEndPoint(IPAddress.Loopback, Options.ListenPort));

    public async Task<TftpRequestEventArgs> NextRequestAsync(TimeSpan? timeout = null) =>
        await _requests.Reader.ReadAsync().AsTask().WaitAsync(timeout ?? DefaultTimeout).ConfigureAwait(false);

    public async Task<TftpFaultEventArgs> NextFaultAsync(TimeSpan? timeout = null) =>
        await _faults.Reader.ReadAsync().AsTask().WaitAsync(timeout ?? DefaultTimeout).ConfigureAwait(false);

    /// <summary>
    /// What the client actually received back, or null if nothing arrived in time. Null is the
    /// assertion that matters for every path that must transmit nothing.
    /// </summary>
    public async Task<byte[]?> ReceiveAsClientAsync(TimeSpan? timeout = null) =>
        (await ReceiveWithSourceAsync(timeout).ConfigureAwait(false)).Datagram;

    /// <summary>
    /// The same receive, keeping the endpoint it came from.
    ///
    /// <para>The source port is not incidental here either: whether a refusal arrives from the
    /// well-known port or from a transfer identifier of its own is the thing
    /// <see cref="TftpRefusalSource"/> exists to make switchable, and a test that cannot see the
    /// port cannot tell the two apart.</para>
    /// </summary>
    public async Task<(byte[]? Datagram, IPEndPoint? From)> ReceiveWithSourceAsync(TimeSpan? timeout = null)
    {
        var buffer = new byte[TftpPacket.MaxDatagramLength];
        using var cts = new CancellationTokenSource(timeout ?? DefaultTimeout);

        try
        {
            SocketReceiveFromResult result = await _client
                .ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), cts.Token)
                .ConfigureAwait(false);

            return (buffer[..result.ReceivedBytes], result.RemoteEndPoint as IPEndPoint);
        }
        catch (OperationCanceledException)
        {
            return (null, null);
        }
        catch (SocketException)
        {
            return (null, null);
        }
    }

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
        _cts.Dispose();
    }

    private static int FreeUdpPort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }

    private async Task RunAsync()
    {
        var listening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Server.Listening += (_, _) => listening.TrySetResult();
        Server.RequestReceived += (_, e) => _requests.Writer.TryWrite(e);
        Server.Fault += (_, e) => _faults.Writer.TryWrite(e);

        _run = Task.Run(() => Server.RunAsync(_cts.Token));

        Task first = await Task.WhenAny(listening.Task, _run).WaitAsync(DefaultTimeout).ConfigureAwait(false);

        if (first == _run)
        {
            // Surfaces the bind failure rather than letting the test time out on a silent socket.
            await _run.ConfigureAwait(false);
            throw new InvalidOperationException("The watch stopped before it started listening.");
        }

        // Binding the client now rather than on its first send, so ClientPort is answerable before
        // a test sends anything and the port it asserts on is the port the request came from.
        _client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
    }
}
