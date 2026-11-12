using System.Globalization;
using System.Net;
using NetControl.Core.Diagnostics;
using NetControl.Core.Reachability;
using NetControl.Core.Safety;

namespace NetControl.Core.Analysis;

/// <summary>
/// Pings a short list of devices on an interval and keeps loss, latency, jitter and outages for
/// each - the tool for "it drops out every so often", which one ping never catches.
///
/// <para><b>Gentle.</b> One echo per target per interval, at most <see cref="MaxInFlight"/> at
/// once, the interval never shorter than <see cref="MinimumInterval"/>, at most
/// <see cref="MaxTargets"/> targets, and every echo through <see cref="NetworkGuard"/> - so
/// listen-only stops it and the global rate cap bounds it. Every target is checked by
/// <see cref="UnicastTarget"/> before the first echo, the same one-host rule as every other probe.</para>
///
/// <para>Memory is fixed: <see cref="HistoryLength"/> samples per target.</para>
/// </summary>
public sealed class PingMonitor : IAsyncDisposable
{
    public const int MaxTargets = 64;

    public const int MaxInFlight = 8;

    public const int HistoryLength = 120;

    /// <summary>Lost in a row before it is called an outage rather than a dropped echo.</summary>
    public const int OutageAfter = 3;

    public static readonly TimeSpan MinimumInterval = TimeSpan.FromMilliseconds(500);

    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(1);

    private readonly IPinger _pinger;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly List<Target> _targets = [];
    private CancellationTokenSource? _run;
    private Task? _loop;

    public PingMonitor(IPinger pinger, TimeProvider? timeProvider = null)
    {
        _pinger = pinger ?? throw new ArgumentNullException(nameof(pinger));
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Raised after each round, on a background thread.</summary>
    public event EventHandler? Updated;

    /// <summary>Raised when a target goes down (true) or comes back (false), on a background thread.</summary>
    public event EventHandler<(IPAddress Address, bool Down, string Message)>? StateChanged;

    /// <summary>Raised when the loop stops because something it cannot work round happened - listen-only mode, say.</summary>
    public event EventHandler<string>? Stopped;

    public bool IsRunning => _loop is { IsCompleted: false };

    public TimeSpan Interval { get; private set; } = DefaultInterval;

    public TimeSpan Timeout { get; private set; } = TimeSpan.FromSeconds(1);

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _targets.Count;
            }
        }
    }

    /// <summary>
    /// Adds a target. Returns null when added, otherwise why not. <paramref name="mask"/> lets the
    /// subnet's own broadcast and network address be refused too.
    /// </summary>
    public string? Add(IPAddress address, string? label = null, IPAddress? mask = null)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (!UnicastTarget.TryCheck(address, mask, out string? problem))
        {
            return problem ?? $"{address} is not one host.";
        }

        lock (_gate)
        {
            if (_targets.Any(t => t.Address.Equals(address)))
            {
                return $"{address} is already being monitored.";
            }

            if (_targets.Count >= MaxTargets)
            {
                return $"The monitor holds {MaxTargets} targets at most - remove one first.";
            }

            _targets.Add(new Target(address, label));
            return null;
        }
    }

    public bool Remove(IPAddress address)
    {
        lock (_gate)
        {
            return _targets.RemoveAll(t => t.Address.Equals(address)) > 0;
        }
    }

    public void ClearTargets()
    {
        lock (_gate)
        {
            _targets.Clear();
        }
    }

    /// <summary>Zeroes every target's statistics, keeping the list.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            foreach (Target target in _targets)
            {
                target.Reset();
            }
        }
    }

    public IReadOnlyList<PingTargetStats> Snapshot()
    {
        lock (_gate)
        {
            return [.. _targets.Select(t => t.Freeze())];
        }
    }

    public void Start(TimeSpan? interval = null, TimeSpan? timeout = null)
    {
        if (IsRunning)
        {
            return;
        }

        Interval = interval is { } i && i > MinimumInterval ? i : (interval is null ? DefaultInterval : MinimumInterval);
        Timeout = timeout is { } t && t > TimeSpan.Zero ? (t < Interval ? t : Interval) : (Interval < TimeSpan.FromSeconds(1) ? Interval : TimeSpan.FromSeconds(1));

        _run = new CancellationTokenSource();
        CancellationToken token = _run.Token;
        _loop = Task.Run(() => LoopAsync(token), CancellationToken.None);
        NetTrace.Log.Info(string.Create(
            CultureInfo.InvariantCulture,
            $"Ping monitor started: {Count} target(s), every {Interval.TotalMilliseconds:0} ms, timeout {Timeout.TotalMilliseconds:0} ms."));
    }

    public async Task StopAsync()
    {
        if (_run is null)
        {
            return;
        }

        await _run.CancelAsync().ConfigureAwait(false);

        try
        {
            if (_loop is not null)
            {
                await _loop.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }

        _run.Dispose();
        _run = null;
        _loop = null;
        NetTrace.Log.Info("Ping monitor stopped.");
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    /// <summary>One round: every target once. Public so a test can drive rounds without a timer.</summary>
    public async Task RoundAsync(CancellationToken cancellationToken = default)
    {
        Target[] targets;
        lock (_gate)
        {
            targets = [.. _targets];
        }

        using var slots = new SemaphoreSlim(MaxInFlight);

        await Task.WhenAll(targets.Select(async target =>
        {
            await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                PingOutcome outcome = await _pinger.PingAsync(target.Address, Timeout, cancellationToken).ConfigureAwait(false);
                Record(target, outcome);
            }
            finally
            {
                slots.Release();
            }
        })).ConfigureAwait(false);

        Updated?.Invoke(this, EventArgs.Empty);
    }

    private async Task LoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(Interval, _time);

        try
        {
            do
            {
                await RoundAsync(token).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Stopped.
        }
        catch (TransmitBlockedException ex)
        {
            NetTrace.Log.Warn("Ping monitor stopped: " + ex.Message);
            Stopped?.Invoke(this, ex.Message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            NetTrace.Log.Error("Ping monitor stopped on an error it did not expect.", ex);
            Stopped?.Invoke(this, $"The ping monitor stopped: {ex.Message}");
        }
    }

    private void Record(Target target, PingOutcome outcome)
    {
        (bool Down, string Message)? change;
        DateTimeOffset now = _time.GetUtcNow();

        lock (_gate)
        {
            change = target.Add(outcome, now);
        }

        if (change is { } c)
        {
            if (c.Down)
            {
                NetTrace.Log.Warn(c.Message);
            }
            else
            {
                NetTrace.Log.Info(c.Message);
            }

            StateChanged?.Invoke(this, (target.Address, c.Down, c.Message));
        }
    }

    private sealed class Target(IPAddress address, string? label)
    {
        private readonly double?[] _history = new double?[HistoryLength];
        private int _next;
        private int _count;
        private long _sent;
        private long _received;
        private double _sum;
        private double? _min;
        private double? _max;
        private double? _last;
        private double? _previousRtt;
        private double _jitterSum;
        private long _jitterSamples;
        private int _consecutiveLost;
        private int _outages;
        private TimeSpan _longest;
        private DateTimeOffset? _firstLostUtc;
        private DateTimeOffset? _downSince;
        private DateTimeOffset? _lastOutageEnd;

        public IPAddress Address { get; } = address;

        public string? Label { get; } = label;

        public void Reset()
        {
            Array.Clear(_history);
            _next = _count = 0;
            _sent = _received = 0;
            _sum = 0;
            _min = _max = _last = _previousRtt = null;
            _jitterSum = 0;
            _jitterSamples = 0;
            _consecutiveLost = _outages = 0;
            _longest = TimeSpan.Zero;
            _firstLostUtc = _downSince = _lastOutageEnd = null;
        }

        public (bool Down, string Message)? Add(PingOutcome outcome, DateTimeOffset now)
        {
            _sent++;
            double? rtt = outcome.Replied ? outcome.RoundTrip?.TotalMilliseconds ?? 0 : null;
            _history[_next] = rtt;
            _next = (_next + 1) % HistoryLength;
            _count = Math.Min(_count + 1, HistoryLength);
            _last = rtt;

            if (rtt is { } ms)
            {
                _received++;
                _sum += ms;
                _min = _min is null ? ms : Math.Min(_min.Value, ms);
                _max = _max is null ? ms : Math.Max(_max.Value, ms);

                if (_previousRtt is { } previous)
                {
                    _jitterSum += Math.Abs(ms - previous);
                    _jitterSamples++;
                }

                _previousRtt = ms;
                _consecutiveLost = 0;
                _firstLostUtc = null;

                if (_downSince is { } since)
                {
                    TimeSpan length = now - since;
                    _longest = length > _longest ? length : _longest;
                    _lastOutageEnd = now;
                    _downSince = null;
                    return (false, string.Create(
                        CultureInfo.InvariantCulture,
                        $"Ping monitor: {Name} is answering again after {length.TotalSeconds:0.#} s."));
                }

                return null;
            }

            _consecutiveLost++;
            _firstLostUtc ??= now;

            if (_consecutiveLost == OutageAfter && _downSince is null)
            {
                _downSince = _firstLostUtc;
                _outages++;
                return (true, $"Ping monitor: {Name} stopped answering ({OutageAfter} echoes lost in a row).");
            }

            return null;
        }

        public PingTargetStats Freeze()
        {
            var history = new double?[_count];
            int start = (_next - _count + HistoryLength) % HistoryLength;
            for (int i = 0; i < _count; i++)
            {
                history[i] = _history[(start + i) % HistoryLength];
            }

            return new PingTargetStats
            {
                Address = Address,
                Label = Label,
                Sent = _sent,
                Received = _received,
                LastMs = _last,
                MinMs = _min,
                MaxMs = _max,
                AverageMs = _received == 0 ? null : _sum / _received,
                JitterMs = _jitterSamples == 0 ? null : _jitterSum / _jitterSamples,
                ConsecutiveLost = _consecutiveLost,
                Outages = _outages,
                LongestOutage = _longest,
                DownSinceUtc = _downSince,
                LastOutageEndUtc = _lastOutageEnd,
                History = history,
            };
        }

        private string Name => Label is null ? Address.ToString() : $"{Label} ({Address})";
    }
}
