using NetControl.Core.Interfaces;
using NetControl.Core.Persistence;
using NetControl.Core.Tftp;

namespace NetControl.App.Serving;

/// <summary>
/// Owns the UDP/69 watch's lifetime, the way <see cref="ServerController"/> owns the DHCP
/// listener's: start, stop, notice when it dies on its own, and hand every event on. The view
/// models never touch a socket.
///
/// <para>A separate controller rather than a second mode of the first one, because the two run at
/// the same time - an image backup is a BOOTP request followed by a TFTP request, and the whole
/// point of the TFTP tab is watching both halves of one attempt.</para>
///
/// <para>Every request and every fault goes into the project's append-only event log through
/// <see cref="TftpEventRecorder"/>, attached for exactly as long as the watch runs - the same rule
/// as the DHCP recorder.</para>
/// </summary>
public sealed class TftpWatchController : IDisposable
{
    private readonly INicInventory _nics;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();

    private TftpWatchServer? _server;
    private TftpEventRecorder? _recorder;
    private CancellationTokenSource? _cancellation;
    private Task? _run;
    private ServerRunState _state = ServerRunState.Stopped;
    private bool _disposed;

    public TftpWatchController(INicInventory nics, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(nics);
        _nics = nics;
        _time = timeProvider ?? TimeProvider.System;
    }

    public event EventHandler? StateChanged;

    public event EventHandler<TftpRequestEventArgs>? RequestReceived;

    public event EventHandler<TftpFaultEventArgs>? Fault;

    public event EventHandler<PersistenceFailedEventArgs>? RecordingFailed;

    /// <summary>Accept mode finished receiving or sending a file.</summary>
    public event EventHandler<TftpTransferEventArgs>? TransferFinished;

    public ServerRunState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public TftpWatchOptions? ActiveOptions { get; private set; }

    /// <summary>Why the watch stopped on its own, or null after an ordinary stop.</summary>
    public string? StopReason { get; private set; }

    /// <summary>
    /// Binds UDP/69 and starts recording. Returns once the socket is bound, or throws the
    /// <see cref="TftpBindException"/> that explains why it could not be - which, on a backup
    /// server, is almost always the TFTP server itself holding the port.
    /// </summary>
    public async Task StartAsync(TftpWatchOptions options, ProjectStore store)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(store);
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            if (_state is not (ServerRunState.Stopped or ServerRunState.Faulted))
            {
                throw new InvalidOperationException(
                    $"The TFTP watch is {_state}. Stop it before starting it again.");
            }

            _state = ServerRunState.Starting;
        }

        StopReason = null;
        StateChanged?.Invoke(this, EventArgs.Empty);

        var server = new TftpWatchServer(_nics, options, _time);
        var recorder = new TftpEventRecorder(store, server);
        var cancellation = new CancellationTokenSource();

        // RunContinuationsAsynchronously for the reason ServerController gives: completing this
        // from the receive loop's thread must not drag the awaiting UI continuation onto it.
        var bound = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnListening(object? sender, EventArgs e) => bound.TrySetResult();

        server.Listening += OnListening;
        server.RequestReceived += OnRequestReceived;
        server.Fault += OnFault;
        server.TransferFinished += OnTransferFinished;
        recorder.RecordingFailed += OnRecordingFailed;

        Task run = Task.Run(() => server.RunAsync(cancellation.Token), CancellationToken.None);
        Task first = await Task.WhenAny(bound.Task, run).ConfigureAwait(false);

        if (first == run)
        {
            // Finished without binding. Awaiting it is what surfaces the TftpBindException.
            server.Listening -= OnListening;
            Detach(server, recorder);
            recorder.Dispose();
            cancellation.Dispose();

            lock (_gate)
            {
                _state = ServerRunState.Stopped;
            }

            StateChanged?.Invoke(this, EventArgs.Empty);
            await run.ConfigureAwait(false);
            return;
        }

        server.Listening -= OnListening;

        lock (_gate)
        {
            _server = server;
            _recorder = recorder;
            _cancellation = cancellation;
            _run = run;
            _state = ServerRunState.Listening;
        }

        ActiveOptions = options;
        StateChanged?.Invoke(this, EventArgs.Empty);

        // Not awaited: this notices the loop ending on its own, so the tab cannot go on showing a
        // watch that has stopped.
        _ = WatchAsync(run);
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cancellation;
        Task? run;

        lock (_gate)
        {
            if (_state is ServerRunState.Stopped or ServerRunState.Faulted)
            {
                return;
            }

            _state = ServerRunState.Stopping;
            cancellation = _cancellation;
            run = _run;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);

        try
        {
            await (cancellation?.CancelAsync() ?? Task.CompletedTask).ConfigureAwait(false);
            if (run is not null)
            {
                await run.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The expected way for a cancelled receive loop to end.
        }
        finally
        {
            Teardown(ServerRunState.Stopped, stopReason: null);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Synchronous, as in ServerController: this runs from application exit.
        _cancellation?.Cancel();
        Teardown(ServerRunState.Stopped, stopReason: null);
    }

    private async Task WatchAsync(Task run)
    {
        string? reason = null;
        try
        {
            await run.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            reason = ex.Message;
        }

        lock (_gate)
        {
            if (!ReferenceEquals(_run, run) || _state is ServerRunState.Stopping or ServerRunState.Stopped)
            {
                return;
            }
        }

        Teardown(
            ServerRunState.Faulted,
            reason ?? "The TFTP watch stopped. There is a fault in the list immediately before this.");
    }

    private void Teardown(ServerRunState state, string? stopReason)
    {
        TftpWatchServer? server;
        TftpEventRecorder? recorder;
        CancellationTokenSource? cancellation;

        lock (_gate)
        {
            server = _server;
            recorder = _recorder;
            cancellation = _cancellation;
            _server = null;
            _recorder = null;
            _cancellation = null;
            _run = null;
            _state = state;
        }

        if (server is not null && recorder is not null)
        {
            Detach(server, recorder);
        }

        recorder?.Dispose();
        cancellation?.Dispose();

        ActiveOptions = null;
        StopReason = stopReason;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Detach(TftpWatchServer server, TftpEventRecorder recorder)
    {
        server.RequestReceived -= OnRequestReceived;
        server.Fault -= OnFault;
        server.TransferFinished -= OnTransferFinished;
        recorder.RecordingFailed -= OnRecordingFailed;
    }

    private void OnRequestReceived(object? sender, TftpRequestEventArgs e) => RequestReceived?.Invoke(this, e);

    private void OnFault(object? sender, TftpFaultEventArgs e) => Fault?.Invoke(this, e);

    private void OnTransferFinished(object? sender, TftpTransferEventArgs e) => TransferFinished?.Invoke(this, e);

    private void OnRecordingFailed(object? sender, PersistenceFailedEventArgs e) =>
        RecordingFailed?.Invoke(this, e);
}
