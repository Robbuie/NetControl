namespace NetControl.Core.Dhcp;

/// <summary>
/// Recognises a repeat of a request already seen, keyed on (transaction id, client MAC) within a
/// short window.
///
/// Devices retransmit aggressively - a PowerFlex that has not been answered will ask several
/// times a second - and without this the log becomes an unreadable wall of identical lines.
///
/// Note what this does NOT do: it does not suppress the reply. A device retransmitting is a
/// device that did not get the last answer, so it gets answered again. Only the log entry is
/// marked as a repeat, so the UI can collapse it into a counter on the existing row.
/// </summary>
public sealed class RetransmitFilter(TimeSpan? window = null, TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Prune when the table gets this big. Commissioning sees tens of devices, not thousands, so
    /// hitting this at all means something is retransmitting pathologically - the bound exists so
    /// that a broken device on the segment cannot grow the table without limit.
    /// </summary>
    private const int PruneThreshold = 512;

    private readonly Dictionary<(uint Xid, MacAddress Mac), long> _lastSeen = new();
    private readonly Lock _gate = new();
    private readonly TimeSpan _window = window ?? DefaultWindow;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Records this request and reports whether an identical one arrived inside the window.
    /// </summary>
    public bool IsRetransmit(uint transactionId, MacAddress mac)
    {
        var key = (transactionId, mac);
        long now = _time.GetTimestamp();

        lock (_gate)
        {
            bool repeat = _lastSeen.TryGetValue(key, out long previous)
                && _time.GetElapsedTime(previous, now) <= _window;

            _lastSeen[key] = now;

            if (_lastSeen.Count > PruneThreshold)
            {
                PruneLocked(now);
            }

            return repeat;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _lastSeen.Clear();
        }
    }

    private void PruneLocked(long now)
    {
        List<(uint Xid, MacAddress Mac)> stale = _lastSeen
            .Where(kv => _time.GetElapsedTime(kv.Value, now) > _window)
            .Select(kv => kv.Key)
            .ToList();

        foreach ((uint, MacAddress) key in stale)
        {
            _lastSeen.Remove(key);
        }
    }
}
