namespace NetControl.Core.Dhcp;

/// <summary>
/// Counts events per client MAC in one-second windows and says when a client is over its allowance.
///
/// <para>Two uses in the server. Requests: a device stuck in a reboot loop, or a misconfigured one
/// with no backoff, can ask hundreds of times a second, and every request handled is a log row, a
/// database row and possibly a reply - so past the allowance requests are counted and summarised
/// rather than handled one by one. Replies: the server must never be the thing on the segment that
/// is broadcasting a hundred times a second, whatever a client does.</para>
///
/// <para>Bounded: at most <see cref="MaxClients"/> MACs are tracked, and idle ones are dropped, so a
/// flood of spoofed source MACs costs a fixed amount of memory.</para>
/// </summary>
public sealed class ClientRateLimiter
{
    /// <summary>MACs tracked at once. Past this the oldest window is forgotten first.</summary>
    public const int MaxClients = 4096;

    private readonly Dictionary<MacAddress, Window> _clients = [];
    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private readonly int _perSecond;
    private long _lastPrune;

    public ClientRateLimiter(int perSecond, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(perSecond, 1);
        _perSecond = perSecond;
        _time = timeProvider ?? TimeProvider.System;
        _lastPrune = _time.GetTimestamp();
    }

    public int PerSecond => _perSecond;

    /// <summary>How many MACs are being tracked right now.</summary>
    public int Tracked
    {
        get
        {
            lock (_gate)
            {
                return _clients.Count;
            }
        }
    }

    /// <summary>Counts one event for <paramref name="mac"/>; false when it is over the allowance.</summary>
    public bool TryTake(MacAddress mac)
    {
        long now = _time.GetTimestamp();

        lock (_gate)
        {
            PruneLocked(now);

            if (!_clients.TryGetValue(mac, out Window? window))
            {
                if (_clients.Count >= MaxClients)
                {
                    EvictOldestLocked();
                }

                window = new Window(now);
                _clients[mac] = window;
            }

            if (_time.GetElapsedTime(window.Start, now) >= TimeSpan.FromSeconds(1))
            {
                window.Start = now;
                window.Count = 0;
            }

            window.LastSeen = now;
            window.Count++;

            if (window.Count <= _perSecond)
            {
                return true;
            }

            window.Suppressed++;
            return false;
        }
    }

    /// <summary>
    /// The clients that went over their allowance since the last call, with how many events each
    /// lost, and resets those counts. For a periodic one-line summary rather than a line per event.
    /// </summary>
    public IReadOnlyList<(MacAddress Mac, long Suppressed)> DrainSuppressed()
    {
        lock (_gate)
        {
            var over = new List<(MacAddress, long)>();

            foreach ((MacAddress mac, Window window) in _clients)
            {
                if (window.Suppressed > 0)
                {
                    over.Add((mac, window.Suppressed));
                    window.Suppressed = 0;
                }
            }

            return over;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _clients.Clear();
        }
    }

    private void PruneLocked(long now)
    {
        // At most once a second, and only when there is something worth pruning: the work is a walk
        // of the table, and doing it per packet is what turns a flood into a CPU problem.
        if (_clients.Count < 256 || _time.GetElapsedTime(_lastPrune, now) < TimeSpan.FromSeconds(1))
        {
            return;
        }

        _lastPrune = now;
        List<MacAddress> idle = [.. _clients
            .Where(kv => kv.Value.Suppressed == 0 && _time.GetElapsedTime(kv.Value.LastSeen, now) > TimeSpan.FromSeconds(10))
            .Select(kv => kv.Key)];

        foreach (MacAddress mac in idle)
        {
            _clients.Remove(mac);
        }
    }

    private void EvictOldestLocked()
    {
        MacAddress? oldest = null;
        long when = long.MaxValue;

        foreach ((MacAddress mac, Window window) in _clients)
        {
            if (window.LastSeen < when)
            {
                when = window.LastSeen;
                oldest = mac;
            }
        }

        if (oldest is { } victim)
        {
            _clients.Remove(victim);
        }
    }

    private sealed class Window(long start)
    {
        public long Start { get; set; } = start;

        public long LastSeen { get; set; } = start;

        public int Count { get; set; }

        public long Suppressed { get; set; }
    }
}
