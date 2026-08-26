using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NetControl.Core.Interfaces;

/// <summary>
/// Live inventory of IPv4-capable adapters.
///
/// Two things are going on here, and they are separate on purpose:
///
/// * A short TTL cache, because <see cref="NetworkInterface.GetAllNetworkInterfaces"/> costs
///   milliseconds and the receive loop asks "which adapter is index 18?" for every datagram.
///   A permanent cache would be wrong too - USB dongles and VPNs come and go mid-session.
/// * A subscription to <see cref="NetworkChange"/>, so the UI learns about an unplugged cable
///   without the user restarting anything. Windows fires these in bursts, so they are coalesced
///   and the event is only raised when the snapshot genuinely differs.
///
/// <see cref="NicsChanged"/> is raised on a background thread. A UI must marshal it.
/// </summary>
public sealed class NicMonitor : INicInventory, IDisposable
{
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long to wait for the burst of notifications around one physical event to settle.
    /// Long enough to coalesce, short enough that the interface bar still feels immediate.
    /// </summary>
    private static readonly TimeSpan CoalesceWindow = TimeSpan.FromMilliseconds(400);

    private readonly Lock _gate = new();
    private readonly TimeSpan _ttl;
    private readonly TimeProvider _time;
    private readonly Timer _coalesce;

    private IReadOnlyList<NicInfo> _all = [];
    private Dictionary<int, NicInfo> _byIndex = new();
    private long _stampTicks = long.MinValue;
    private bool _subscribed;
    private bool _disposed;

    public NicMonitor(TimeSpan? ttl = null, TimeProvider? timeProvider = null)
    {
        _ttl = ttl ?? DefaultTtl;
        _time = timeProvider ?? TimeProvider.System;
        _coalesce = new Timer(_ => PublishIfChanged(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public event EventHandler<NicsChangedEventArgs>? NicsChanged;

    /// <summary>
    /// Begin watching for adapter changes. Separate from the constructor because subscribing has
    /// a side effect on a process-wide event and callers that only want a one-off inventory
    /// should not pay for it.
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_subscribed)
            {
                return;
            }

            NetworkChange.NetworkAddressChanged += OnNetworkChanged;
            NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
            _subscribed = true;
        }

        Refresh();
    }

    /// <summary>Current inventory, ordered by interface index. Refreshed if the cache is stale.</summary>
    public IReadOnlyList<NicInfo> Snapshot()
    {
        lock (_gate)
        {
            RefreshIfStaleLocked();
            return _all;
        }
    }

    /// <summary>
    /// The adapter a datagram arrived on. Null means the index is unknown to us, which normally
    /// means the adapter disappeared between the packet arriving and this lookup.
    /// </summary>
    public NicInfo? ByIndex(int index)
    {
        lock (_gate)
        {
            RefreshIfStaleLocked();
            return _byIndex.GetValueOrDefault(index);
        }
    }

    /// <summary>
    /// Resolves what a user typed: an interface index, an exact adapter name, or a substring of
    /// either the name or the driver description ("I219" for "Intel(R) Ethernet Connection I219-V").
    /// </summary>
    public NicInfo? Find(string nameOrIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nameOrIndex);
        string needle = nameOrIndex.Trim();

        if (int.TryParse(needle, out int index))
        {
            NicInfo? byIndex = ByIndex(index);
            if (byIndex is not null)
            {
                return byIndex;
            }
        }

        IReadOnlyList<NicInfo> all = Snapshot();
        return all.FirstOrDefault(n => n.Name.Equals(needle, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(n => n.Description.Equals(needle, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(n => n.Description.Contains(needle, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(n => n.Name.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Force a re-read now, ignoring the TTL, and raise the event if anything moved.</summary>
    public void Refresh() => PublishIfChanged();

    /// <summary>
    /// One-shot inventory with no caching or subscription. Every IPv4-capable, non-loopback
    /// adapter, keyed by the index the IP stack reports.
    /// </summary>
    public static IReadOnlyList<NicInfo> Enumerate()
    {
        var list = new List<NicInfo>();

        foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!ni.Supports(NetworkInterfaceComponent.IPv4))
            {
                continue;
            }

            // A device can never send us a BOOTP request over loopback, and on a machine full of
            // virtual adapters every row we can honestly remove is worth removing.
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            IPInterfaceProperties properties;
            IPv4InterfaceProperties? v4;
            try
            {
                properties = ni.GetIPProperties();
                v4 = properties.GetIPv4Properties();
            }
            catch (NetworkInformationException)
            {
                // An adapter can be torn down between enumeration and query. Skipping one row is
                // the right answer; throwing out of an inventory call is not.
                continue;
            }

            if (v4 is null)
            {
                continue;
            }

            UnicastIPAddressInformation? unicast = properties.UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);

            long speed;
            try
            {
                speed = ni.Speed;
            }
            catch (PlatformNotSupportedException)
            {
                speed = -1;
            }
            catch (NetworkInformationException)
            {
                speed = -1;
            }

            list.Add(new NicInfo(
                v4.Index,
                ni.Name,
                ni.Description,
                MacAddress.FromPhysicalAddress(ni.GetPhysicalAddress()),
                unicast?.Address,
                unicast?.IPv4Mask,
                ni.OperationalStatus,
                ni.NetworkInterfaceType,
                speed));
        }

        list.Sort((a, b) => a.Index.CompareTo(b.Index));
        return list;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_subscribed)
            {
                NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
                NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
                _subscribed = false;
            }
        }

        _coalesce.Dispose();
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => ScheduleRefresh();

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => ScheduleRefresh();

    private void ScheduleRefresh()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            // Restarting the one-shot timer on each notification is the coalescing: the callback
            // only runs once the burst has been quiet for the whole window.
            _coalesce.Change(CoalesceWindow, Timeout.InfiniteTimeSpan);
        }
    }

    private void PublishIfChanged()
    {
        IReadOnlyList<NicInfo> previous;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            previous = _all;
        }

        // Enumerating costs milliseconds, so it happens outside the lock: the receive loop calls
        // ByIndex for every datagram and must not queue behind an inventory sweep. A concurrent
        // refresh can therefore win the race and store first; the loser's data is at most one
        // TTL stale, which is the same guarantee the cache already gives.
        IReadOnlyList<NicInfo> current = Enumerate();

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            StoreLocked(current);
        }

        // NicInfo is a record, so this compares by value - Windows fires several notifications
        // for one cable event and only the first of them actually changes anything.
        if (previous.SequenceEqual(current))
        {
            return;
        }

        NicsChanged?.Invoke(this, new NicsChangedEventArgs(current, previous));
    }

    private void RefreshIfStaleLocked()
    {
        long now = _time.GetTimestamp();
        if (_stampTicks != long.MinValue && _time.GetElapsedTime(_stampTicks, now) < _ttl)
        {
            return;
        }

        StoreLocked(Enumerate());
    }

    private void StoreLocked(IReadOnlyList<NicInfo> nics)
    {
        _all = nics;

        // Group rather than ToDictionary: a duplicate index should never happen, but a startup
        // crash is a far worse outcome than quietly preferring the first match.
        _byIndex = nics.GroupBy(n => n.Index).ToDictionary(g => g.Key, g => g.First());
        _stampTicks = _time.GetTimestamp();
    }
}
