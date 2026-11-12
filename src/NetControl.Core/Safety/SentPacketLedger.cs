using System.Globalization;
using System.Text;

namespace NetControl.Core.Safety;

/// <summary>
/// Every transmission the application made, newest kept, oldest dropped.
///
/// <para>The answer to "what did your tool just do to my network?" - which is a question a controls
/// engineer gets asked on a running plant, and one this tool should be able to answer with a list
/// rather than an assurance. Bounded so that a long session costs a fixed amount of memory; the
/// per-kind totals keep counting after the oldest entries have gone.</para>
/// </summary>
public sealed class SentPacketLedger
{
    /// <summary>Entries kept. A full subnet sweep of a /22 is about a thousand.</summary>
    public const int DefaultCapacity = 5000;

    private readonly SentPacket?[] _entries;
    private readonly Lock _gate = new();
    private readonly long[] _sent = new long[Enum.GetValues<TransmitKind>().Length];
    private readonly long[] _blocked = new long[Enum.GetValues<TransmitKind>().Length];
    private int _next;
    private int _count;

    public SentPacketLedger(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _entries = new SentPacket?[capacity];
    }

    /// <summary>Raised after each entry, on the transmitting thread. Handlers marshal for themselves.</summary>
    public event EventHandler<SentPacket>? Added;

    public int Capacity => _entries.Length;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    /// <summary>Everything sent since the application started, including entries no longer kept.</summary>
    public long TotalSent
    {
        get
        {
            lock (_gate)
            {
                return _sent.Sum();
            }
        }
    }

    /// <summary>Everything listen-only mode stopped since the application started.</summary>
    public long TotalBlocked
    {
        get
        {
            lock (_gate)
            {
                return _blocked.Sum();
            }
        }
    }

    public void Add(SentPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);

        lock (_gate)
        {
            _entries[_next] = packet;
            _next = (_next + 1) % _entries.Length;
            _count = Math.Min(_count + 1, _entries.Length);

            if (packet.Blocked)
            {
                _blocked[(int)packet.Kind]++;
            }
            else
            {
                _sent[(int)packet.Kind]++;
            }
        }

        try
        {
            Added?.Invoke(this, packet);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A window showing the ledger is not a reason for a transmission to fail.
        }
    }

    /// <summary>The kept entries, oldest first.</summary>
    public IReadOnlyList<SentPacket> Snapshot()
    {
        lock (_gate)
        {
            var list = new List<SentPacket>(_count);
            int start = (_next - _count + _entries.Length) % _entries.Length;

            for (int i = 0; i < _count; i++)
            {
                if (_entries[(start + i) % _entries.Length] is { } entry)
                {
                    list.Add(entry);
                }
            }

            return list;
        }
    }

    /// <summary>Totals per kind since start: (kind, sent, blocked), kinds with nothing omitted.</summary>
    public IReadOnlyList<(TransmitKind Kind, long Sent, long Blocked)> Totals()
    {
        lock (_gate)
        {
            return [.. Enum.GetValues<TransmitKind>()
                .Select(k => (k, _sent[(int)k], _blocked[(int)k]))
                .Where(t => t.Item2 > 0 || t.Item3 > 0)];
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_entries);
            _next = 0;
            _count = 0;
        }
    }

    /// <summary>The kept entries as CSV, for a support bundle or to hand to whoever asked.</summary>
    public string ToCsv()
    {
        var text = new StringBuilder("Utc,Kind,Destination,Purpose,Bytes,Blocked\r\n");

        foreach (SentPacket p in Snapshot())
        {
            text.Append(p.Utc.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append(',')
                .Append(p.Kind).Append(',')
                .Append(Quote(p.Destination)).Append(',')
                .Append(Quote(p.Purpose)).Append(',')
                .Append(p.Bytes.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(p.Blocked ? "yes" : "no").Append("\r\n");
        }

        return text.ToString();
    }

    private static string Quote(string value) =>
        value.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
}
