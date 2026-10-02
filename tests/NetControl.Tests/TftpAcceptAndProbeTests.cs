using System.Net;
using System.Net.Sockets;
using NetControl.Core.Tftp;
using Xunit;

namespace NetControl.Tests;

/// <summary>
/// Accept mode and the probe, run against each other over loopback: the probe writes and reads back
/// as a client, Accept mode receives and serves as a server, and both sides are the shipping code.
///
/// <para>That pairing is the point. Each side was written from RFC 1350 by the same hand, so a test
/// of either against itself would pass a shared misreading. Against each other they at least have to
/// agree on the wire - and the rollover test makes them agree past block 65,535, which is the part a
/// real server is most likely to get wrong and the part a hand-checked test is least likely to reach.</para>
/// </summary>
public sealed class TftpAcceptAndProbeTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "netcontrol-tests", "accept-" + Guid.NewGuid().ToString("N"));

    public TftpAcceptAndProbeTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // A transfer socket may still hold a file for a moment after a failed test.
        }
    }

    [Fact]
    public async Task A_probe_writes_into_accept_mode_and_reads_back_an_identical_copy()
    {
        await using AcceptServer server = await AcceptServer.StartAsync(_folder);

        TftpProbeResult result = await TftpProbe.RunAsync(Probe(server.Port, "probe.bin", 100_000));

        Assert.True(result.Succeeded, result.Summary);
        Assert.True(result.ReadBackMatched);
        Assert.Contains("identical copy", result.Summary, StringComparison.Ordinal);

        string written = Path.Combine(_folder, "probe.bin");
        byte[] bytes = await File.ReadAllBytesAsync(written);
        Assert.Equal(100_000, bytes.Length);
        Assert.Equal(TftpProbePattern.At(0), bytes[0]);
        Assert.Equal(TftpProbePattern.At(99_999), bytes[99_999]);

        // The probe leaves its file and says so - it never deletes anything.
        Assert.Contains(result.Findings, f => f.Contains("never deletes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Both_ends_survive_the_block_counter_rolling_over()
    {
        await using AcceptServer server = await AcceptServer.StartAsync(_folder);

        // At the smallest legal block size 65,536 blocks is only 512 KB, so the rollover can be
        // crossed in a test. Both the write and the read-back go past it.
        TftpProbeOptions options = Probe(server.Port, "rollover.bin", 600_000) with { BlockSize = TftpLimits.MinBlockSize };

        TftpProbeResult result = await TftpProbe.RunAsync(options);

        Assert.True(result.Succeeded, result.Summary);
        Assert.True(result.ReadBackMatched);
        Assert.Equal(1, result.Write!.Stats.Wraps);
        Assert.Equal(1, result.Read!.Stats.Wraps);
        Assert.False(result.Read.Stats.PeerRolledOverToOne);
        Assert.Contains("rolling over", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_existing_file_is_refused_when_overwrite_is_off_and_the_probe_says_why()
    {
        await File.WriteAllTextAsync(Path.Combine(_folder, "taken.bin"), "a real backup");
        await using AcceptServer server = await AcceptServer.StartAsync(_folder);

        TftpProbeResult result = await TftpProbe.RunAsync(Probe(server.Port, "taken.bin", 1_000));

        Assert.False(result.Succeeded);
        Assert.False(result.WriteAccepted);
        Assert.Contains("refused", result.Summary, StringComparison.Ordinal);
        Assert.Equal("a real backup", await File.ReadAllTextAsync(Path.Combine(_folder, "taken.bin")));

        TftpRequestEventArgs request = await server.NextRequestAsync();
        Assert.Equal(TftpWatchAction.Refused, request.Action);
        Assert.Contains("overwriting is off", request.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Overwrite_on_replaces_the_file()
    {
        await File.WriteAllTextAsync(Path.Combine(_folder, "again.bin"), "old");
        await using AcceptServer server = await AcceptServer.StartAsync(_folder, allowOverwrite: true);

        TftpProbeResult result = await TftpProbe.RunAsync(Probe(server.Port, "again.bin", 5_000));

        Assert.True(result.Succeeded, result.Summary);
        Assert.Equal(5_000, new FileInfo(Path.Combine(_folder, "again.bin")).Length);
    }

    [Fact]
    public async Task A_missing_subfolder_is_refused_in_the_words_a_real_server_would_need()
    {
        await using AcceptServer server = await AcceptServer.StartAsync(_folder);

        TftpProbeResult result = await TftpProbe.RunAsync(Probe(server.Port, "ROBOT1/FROM00.IMG", 1_000));

        Assert.False(result.Succeeded);
        TftpRequestEventArgs request = await server.NextRequestAsync();
        Assert.Equal(TftpWatchAction.Refused, request.Action);
        Assert.Contains("ROBOT1", request.Reason, StringComparison.Ordinal);
        Assert.Contains("does not exist", request.Reason, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_folder, "ROBOT1")));
    }

    [Fact]
    public async Task A_received_backup_is_reported_with_its_path_and_statistics()
    {
        await using AcceptServer server = await AcceptServer.StartAsync(_folder);

        TftpProbeResult result = await TftpProbe.RunAsync(
            Probe(server.Port, "FROM00.IMG", 20_000) with { ReadBack = false });
        Assert.True(result.Succeeded, result.Summary);

        TftpTransferEventArgs finished = await server.NextTransferAsync();
        Assert.True(finished.IsWrite);
        Assert.True(finished.Outcome.Succeeded);
        Assert.Equal(20_000, finished.Outcome.Stats.Bytes);
        Assert.Equal(Path.Combine(_folder, "FROM00.IMG"), finished.Path);

        // Written under a temporary name and renamed: nothing called .partial is left behind.
        Assert.Empty(Directory.GetFiles(_folder, "*.partial"));
    }

    [Fact]
    public async Task A_probe_at_a_port_nobody_listens_on_says_so()
    {
        int port = FreeUdpPort();

        TftpProbeResult result = await TftpProbe.RunAsync(
            Probe(port, "nobody.bin", 1_000) with { Timeout = TimeSpan.FromMilliseconds(200), MaxRetries = 1 });

        Assert.False(result.Succeeded);
        Assert.False(result.WriteAccepted);

        // Windows reports the closed port; anywhere that does not, the timeout wording applies.
        Assert.True(
            result.Summary.Contains("Nothing is listening", StringComparison.Ordinal)
            || result.Summary.Contains("No answer", StringComparison.Ordinal),
            result.Summary);
    }

    [Fact]
    public async Task Accept_mode_will_not_start_without_a_folder()
    {
        var server = new TftpWatchServer(
            new FakeNicInventory(),
            new TftpWatchOptions { Mode = TftpWatchMode.Accept, ListenPort = 0 });

        TftpBindException thrown = await Assert.ThrowsAsync<TftpBindException>(() => server.RunAsync());

        Assert.Contains("needs a folder", thrown.Message, StringComparison.Ordinal);
        Assert.False(server.IsListening);
    }

    private static TftpProbeOptions Probe(int port, string fileName, long size) => new()
    {
        Server = IPAddress.Loopback,
        ServerPort = port,
        FileName = fileName,
        SizeBytes = size,
        Timeout = TimeSpan.FromSeconds(1),
    };

    private static int FreeUdpPort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }

    /// <summary>A real <see cref="TftpWatchServer"/> in Accept mode on a spare loopback port.</summary>
    private sealed class AcceptServer : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly System.Threading.Channels.Channel<TftpRequestEventArgs> _requests =
            System.Threading.Channels.Channel.CreateUnbounded<TftpRequestEventArgs>();
        private readonly System.Threading.Channels.Channel<TftpTransferEventArgs> _transfers =
            System.Threading.Channels.Channel.CreateUnbounded<TftpTransferEventArgs>();
        private Task _run = Task.CompletedTask;

        private AcceptServer(int port) => Port = port;

        public int Port { get; }

        public static async Task<AcceptServer> StartAsync(string folder, bool allowOverwrite = false)
        {
            var harness = new AcceptServer(FreeUdpPort());
            var server = new TftpWatchServer(
                new FakeNicInventory(),
                new TftpWatchOptions
                {
                    ListenPort = harness.Port,
                    Mode = TftpWatchMode.Accept,
                    AcceptFolder = folder,
                    AllowOverwrite = allowOverwrite,
                    TransferTimeout = TimeSpan.FromSeconds(1),
                });

            var listening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            server.Listening += (_, _) => listening.TrySetResult();
            server.RequestReceived += (_, e) => harness._requests.Writer.TryWrite(e);
            server.TransferFinished += (_, e) => harness._transfers.Writer.TryWrite(e);

            harness._run = Task.Run(() => server.RunAsync(harness._stop.Token));
            Task first = await Task.WhenAny(listening.Task, harness._run);
            if (first == harness._run)
            {
                await harness._run;
            }

            return harness;
        }

        public Task<TftpRequestEventArgs> NextRequestAsync() =>
            _requests.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        public Task<TftpTransferEventArgs> NextTransferAsync() =>
            _transfers.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            try
            {
                await _run.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException)
            {
                // Stopped, as asked.
            }

            _stop.Dispose();
        }
    }
}
