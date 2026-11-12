using System.Diagnostics;
using System.Globalization;
using NetControl.Core.Diagnostics;

namespace NetControl.Core.Safety;

/// <summary>
/// The one gate every transmission in the application passes through.
///
/// <para><b>Listen only.</b> While <see cref="ListenOnly"/> is on, nothing is sent: a scan, a ping,
/// a CIP write, a DHCP reply, a TFTP refusal and a PROFINET frame all stop at this gate with a
/// <see cref="TransmitBlockedException"/> that says so. Watching, capturing and reading local state
/// (the ARP table, this PC's adapters) keep working - that is the point of the switch: a tool that
/// can be put on a running line knowing it cannot disturb anything.</para>
///
/// <para><b>A rate cap.</b> <see cref="MaxPacketsPerSecond"/> bounds everything the application
/// originates, across every feature at once, with a token bucket that allows a one-second burst.
/// The per-feature limits (one broadcast per scan, a sweep's own pacing, a TCP connection at a
/// time) stay as they were; this is the backstop for when two features run together, or a device
/// asks for an address a hundred times a second. A caller over the cap waits - it is not refused -
/// so a cap can make an operation slower and never makes it fail.</para>
///
/// <para><b>A ledger.</b> Every transmission, allowed or blocked, is recorded in <see cref="Ledger"/>.</para>
///
/// <para>Ambient, as <see cref="Shared"/>, for the same reason as <see cref="NetTrace"/>: the engines
/// are constructed in many places. A test that needs its own can construct one; the engines take an
/// optional guard and fall back to <see cref="Shared"/>.</para>
/// </summary>
public sealed class NetworkGuard
{
    /// <summary>The cap a fresh install starts with. Generous for one engineer, gentle for a plant network.</summary>
    public const int DefaultMaxPacketsPerSecond = 200;

    /// <summary>The lowest cap that can be set. Below this a scan of one subnet takes minutes.</summary>
    public const int MinimumMaxPacketsPerSecond = 10;

    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private double _tokens;
    private long _lastRefill;
    private int _maxPacketsPerSecond = DefaultMaxPacketsPerSecond;
    private volatile bool _listenOnly;

    public NetworkGuard(SentPacketLedger? ledger = null, TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        Ledger = ledger ?? new SentPacketLedger();
        _tokens = _maxPacketsPerSecond;
        _lastRefill = _time.GetTimestamp();
    }

    /// <summary>The guard the application's engines use.</summary>
    public static NetworkGuard Shared { get; } = new();

    /// <summary>Raised when <see cref="ListenOnly"/> or <see cref="MaxPacketsPerSecond"/> changes.</summary>
    public event EventHandler? Changed;

    public SentPacketLedger Ledger { get; }

    /// <summary>True: nothing is transmitted. See the class remarks.</summary>
    public bool ListenOnly
    {
        get => _listenOnly;
        set
        {
            if (_listenOnly == value)
            {
                return;
            }

            _listenOnly = value;
            NetTrace.Log.Info(value
                ? "Listen-only mode on: nothing will be transmitted until it is turned off."
                : "Listen-only mode off: transmissions allowed again.");
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// The most the application originates per second, every feature together. Zero means no cap.
    /// Values between 1 and <see cref="MinimumMaxPacketsPerSecond"/> are raised to the minimum.
    /// </summary>
    public int MaxPacketsPerSecond
    {
        get => _maxPacketsPerSecond;
        set
        {
            int normalised = value <= 0 ? 0 : Math.Max(value, MinimumMaxPacketsPerSecond);

            lock (_gate)
            {
                if (_maxPacketsPerSecond == normalised)
                {
                    return;
                }

                _maxPacketsPerSecond = normalised;
                _tokens = Math.Min(_tokens, normalised);
            }

            NetTrace.Log.Info(normalised == 0
                ? "Send rate cap removed."
                : string.Create(CultureInfo.InvariantCulture, $"Send rate capped at {normalised} packets a second."));
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Waits for room under the cap, records the transmission, and returns - or, in listen-only mode,
    /// records it as blocked and throws <see cref="TransmitBlockedException"/>. Call immediately before
    /// the send.
    /// </summary>
    public async ValueTask AcquireAsync(
        TransmitKind kind,
        string destination,
        string purpose,
        int bytes = 0,
        CancellationToken cancellationToken = default)
    {
        ThrowIfListenOnly(kind, destination, purpose);

        TimeSpan wait;
        while ((wait = TakeToken()) > TimeSpan.Zero)
        {
            await Task.Delay(wait, _time, cancellationToken).ConfigureAwait(false);
        }

        Record(kind, destination, purpose, bytes, blocked: false);
    }

    /// <summary>
    /// The synchronous form of <see cref="AcquireAsync"/>, for the socket loops that send inline.
    /// Waits by sleeping the calling thread, so it is only for threads that own their own loop.
    /// </summary>
    public void Acquire(TransmitKind kind, string destination, string purpose, int bytes = 0)
    {
        ThrowIfListenOnly(kind, destination, purpose);

        TimeSpan wait;
        while ((wait = TakeToken()) > TimeSpan.Zero)
        {
            Thread.Sleep(wait);
        }

        Record(kind, destination, purpose, bytes, blocked: false);
    }

    /// <summary>
    /// The listen-only check alone, with no rate cap and no ledger entry when allowed. For the
    /// packets inside an exchange the user already started - a TFTP transfer's acknowledgements -
    /// which are paced by the other end and would flood the ledger one entry each.
    /// </summary>
    public void ThrowIfListenOnly(TransmitKind kind, string destination, string purpose)
    {
        if (!_listenOnly)
        {
            return;
        }

        Record(kind, destination, purpose, 0, blocked: true);
        throw new TransmitBlockedException(kind, destination, purpose);
    }

    /// <summary>Takes one token, or returns how long to wait for one.</summary>
    private TimeSpan TakeToken()
    {
        lock (_gate)
        {
            int cap = _maxPacketsPerSecond;
            if (cap <= 0)
            {
                return TimeSpan.Zero;
            }

            long now = _time.GetTimestamp();
            double elapsed = _time.GetElapsedTime(_lastRefill, now).TotalSeconds;
            _lastRefill = now;
            _tokens = Math.Min(cap, _tokens + (elapsed * cap));

            if (_tokens >= 1)
            {
                _tokens -= 1;
                return TimeSpan.Zero;
            }

            double seconds = (1 - _tokens) / cap;
            return TimeSpan.FromSeconds(Math.Max(seconds, 0.001));
        }
    }

    private void Record(TransmitKind kind, string destination, string purpose, int bytes, bool blocked)
    {
        var packet = new SentPacket(_time.GetUtcNow(), kind, destination, purpose, bytes, blocked);
        Ledger.Add(packet);

        ITraceLog log = NetTrace.Log;
        if (blocked)
        {
            log.Warn($"Blocked by listen-only mode: {TransmitBlockedException.Describe(kind)} to {destination} ({purpose}).");
        }
        else if (log.IsVerbose)
        {
            log.WriteVerbose(string.Create(
                CultureInfo.InvariantCulture,
                $"SEND {kind} -> {destination} ({purpose}){(bytes > 0 ? $", {bytes} bytes" : string.Empty)}"));
        }
    }

    /// <summary>For tests: a stopwatch-free way to describe the cap.</summary>
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    internal double TokensForTest
    {
        get
        {
            lock (_gate)
            {
                return _tokens;
            }
        }
    }
}
