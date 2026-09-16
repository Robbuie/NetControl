using System.Net;

namespace NetControl.Core.Tftp;

/// <summary>
/// Recognises a repeat of a request already seen, keyed on the client's transfer identifier and
/// the filename it asked for.
///
/// <para>The same job <c>RetransmitFilter</c> does on the DHCP side, and deliberately not the same
/// class: that one is keyed on <c>(transaction id, MAC)</c>, which TFTP has neither of. What
/// identifies one attempt here is the source endpoint - address and port together are the client's
/// TID, fixed for the life of an attempt and different on the next one - with the filename beside
/// it so two different requests from one port can never collapse into each other.</para>
///
/// <para>Note what this does NOT do, for the same reason the DHCP one does not: it does not
/// suppress the answer. A client retransmitting is a client that did not get the last refusal, so
/// it gets refused again. Only the log entry is marked, so the UI can collapse it into a counter
/// rather than filling the screen with one controller asking eight times.</para>
/// </summary>
public sealed class TftpRetransmitFilter(TimeSpan? window = null, TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(6);

    /// <summary>
    /// Prune when the table gets this big. A commissioning session sees a handful of controllers,
    /// so reaching this means something is retransmitting pathologically - the bound exists so a
    /// broken device on the segment cannot grow the table without limit.
    /// </summary>
    private const int PruneThreshold = 512;

    private readonly Dictionary<(IPAddress Address, int Port, string FileName), long> _lastSeen = new();
    private readonly Lock _gate = new();
    private readonly TimeSpan _window = window ?? DefaultWindow;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>Records this request and reports whether an identical one arrived inside the window.</summary>
    public bool IsRetransmit(IPEndPoint source, string fileName)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(fileName);

        // Ordinal, not case-insensitive: the filename is compared the way it is reported, and two
        // requests differing only in case are two different requests as far as this tool is
        // concerned - deciding they are the same is the server's business, not ours.
        var key = (source.Address, source.Port, fileName);
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
        List<(IPAddress Address, int Port, string FileName)> stale = _lastSeen
            .Where(kv => _time.GetElapsedTime(kv.Value, now) > _window)
            .Select(kv => kv.Key)
            .ToList();

        foreach ((IPAddress, int, string) key in stale)
        {
            _lastSeen.Remove(key);
        }
    }
}
