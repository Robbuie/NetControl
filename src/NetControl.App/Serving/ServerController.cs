using NetControl.Core.Dhcp;
using NetControl.Core.Interfaces;
using NetControl.Core.Persistence;

namespace NetControl.App.Serving;

/// <summary>
/// Owns the listener's lifetime, so the view models never do.
///
/// <para>A <see cref="DhcpServer"/> takes its options at construction and they are immutable, which
/// is right for the engine and awkward for a UI where the user changes adapter and flips between
/// Watch and Serve. The answer here is that changing anything stops the old server and builds a
/// new one - explicitly, visibly, and with a fresh <see cref="DhcpEventRecorder"/> attached. That
/// is a feature rather than a workaround: a tool that silently re-aims a running DHCP server on a
/// plant network is a tool nobody should trust.</para>
///
/// <para>Every event here is re-raised from the one the current server owns, so a subscriber
/// subscribes once and keeps working across restarts. They arrive on the receive loop's thread,
/// exactly as they do from Core - marshalling is the view model's job, not this class's, because
/// the CLI will want these same events with no dispatcher anywhere.</para>
/// </summary>
public sealed class ServerController : IDisposable
{
    private readonly INicInventory _nics;
    private readonly IAssignmentPolicy _policy;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();

    private DhcpServer? _server;
    private DhcpEventRecorder? _recorder;
    private CancellationTokenSource? _cancellation;
    private Task? _run;
    private ServerRunState _state = ServerRunState.Stopped;
    private bool _disposed;

    public ServerController(INicInventory nics, IAssignmentPolicy policy, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(nics);
        ArgumentNullException.ThrowIfNull(policy);

        _nics = nics;
        _policy = policy;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Raised whenever <see cref="State"/> changes. May arrive on a background thread.</summary>
    public event EventHandler? StateChanged;

    public event EventHandler<DhcpRequestEventArgs>? RequestReceived;

    public event EventHandler<DhcpReplyEventArgs>? ReplySent;

    public event EventHandler<DhcpFaultEventArgs>? Fault;

    /// <summary>
    /// A row that could not be written to the project. The server carries on serving; this is how
    /// the UI stops claiming to be recording when it is not.
    /// </summary>
    public event EventHandler<PersistenceFailedEventArgs>? RecordingFailed;

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

    /// <summary>The options the running server was built with, or null when nothing is running.</summary>
    public DhcpServerOptions? ActiveOptions { get; private set; }

    /// <summary>
    /// Why the listener stopped on its own, when it did. Cleared on the next successful start so
    /// a stale explanation cannot linger next to a healthy server.
    /// </summary>
    public string? StopReason { get; private set; }

    /// <summary>How many rows the current run has written, and how many failed to write.</summary>
    public DhcpEventRecorder? Recorder => _recorder;

    /// <summary>
    /// Binds and starts receiving. Returns once the socket is bound, so a caller that gets back
    /// without an exception knows the tool is genuinely listening rather than about to discover
    /// it is not.
    /// </summary>
    /// <exception cref="DhcpBindException">
    /// The port could not be taken, or something holds it in a way that makes delivery ambiguous.
    /// Carries the remediation text; show it verbatim.
    /// </exception>
    public async Task StartAsync(DhcpServerOptions options, ProjectStore store)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(store);
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            if (_state is not (ServerRunState.Stopped or ServerRunState.Faulted))
            {
                throw new InvalidOperationException(
                    $"The listener is {_state}. Stop it before starting it again.");
            }

            _state = ServerRunState.Starting;
        }

        StopReason = null;
        StateChanged?.Invoke(this, EventArgs.Empty);

        var server = new DhcpServer(_nics, _policy, options, _time);
        var recorder = new DhcpEventRecorder(store, server);
        var cancellation = new CancellationTokenSource();

        // RunContinuationsAsynchronously so that completing this from the receive loop's thread
        // cannot drag the awaiting UI continuation onto it.
        var bound = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnListening(object? sender, EventArgs e) => bound.TrySetResult();

        server.Listening += OnListening;
        server.RequestReceived += OnRequestReceived;
        server.ReplySent += OnReplySent;
        server.Fault += OnFault;
        recorder.RecordingFailed += OnRecordingFailed;

        Task run = Task.Run(() => server.RunAsync(cancellation.Token), CancellationToken.None);

        Task first = await Task.WhenAny(bound.Task, run).ConfigureAwait(false);

        if (first == run)
        {
            // It finished without ever binding. Awaiting is what turns a faulted task into the
            // DhcpBindException the caller is meant to show.
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

        // Deliberately not awaited: this watches for the loop ending on its own - a socket that
        // died under us, or an adapter that was removed - so the UI cannot keep showing a green
        // light over a server that has stopped.
        _ = WatchAsync(run);
    }

    /// <summary>Cancels the receive loop and waits for it to unwind. Safe to call when stopped.</summary>
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

        // Synchronous on purpose: this runs from application exit, where an async teardown would
        // race the dispatcher shutting down. Cancel, let the loop notice, and release the socket.
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
            // A normal Stop is already tearing this down and will set the state itself. Checking
            // the task by reference as well guards the case where a stop and a restart have both
            // happened while this continuation was queued.
            if (!ReferenceEquals(_run, run) || _state is ServerRunState.Stopping or ServerRunState.Stopped)
            {
                return;
            }
        }

        // Teardown does the unsubscribing and disposing, so this does not duplicate it.
        Teardown(
            ServerRunState.Faulted,
            reason ?? "The listener stopped. There is a fault in the log immediately before this.");
    }

    private void Teardown(ServerRunState state, string? stopReason)
    {
        DhcpServer? server;
        DhcpEventRecorder? recorder;
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

    private void Detach(DhcpServer server, DhcpEventRecorder recorder)
    {
        server.RequestReceived -= OnRequestReceived;
        server.ReplySent -= OnReplySent;
        server.Fault -= OnFault;
        recorder.RecordingFailed -= OnRecordingFailed;
    }

    private void OnRequestReceived(object? sender, DhcpRequestEventArgs e) => RequestReceived?.Invoke(this, e);

    private void OnReplySent(object? sender, DhcpReplyEventArgs e) => ReplySent?.Invoke(this, e);

    private void OnFault(object? sender, DhcpFaultEventArgs e) => Fault?.Invoke(this, e);

    private void OnRecordingFailed(object? sender, PersistenceFailedEventArgs e) =>
        RecordingFailed?.Invoke(this, e);
}
