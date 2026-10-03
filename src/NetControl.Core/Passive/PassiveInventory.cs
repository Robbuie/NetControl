using System.Net;
using NetControl.Core.Oui;

namespace NetControl.Core.Passive;

/// <summary>
/// Folds sightings into one row per MAC. Thread-safe: frames arrive on the capture thread and the
/// window reads snapshots on its own.
///
/// <para>Addresses are ranked by how they were learned. ARP and DHCP state an address about the
/// sender itself; ordinary IP traffic only says which address a frame came from, which for a router
/// is everybody beyond it. So a MAC heard both ways lists its ARP address first, and a MAC with many
/// traffic addresses and no ARP one is flagged as a router rather than listed as a device with
/// forty addresses.</para>
/// </summary>
public sealed class PassiveInventory
{
    /// <summary>Kept per device. A router can source thousands; the list is for reading, not counting.</summary>
    private const int MaxAddresses = 16;

    private const int MaxNames = 4;

    private readonly IOuiLookup? _oui;
    private readonly Dictionary<MacAddress, Entry> _devices = [];
    private readonly Lock _gate = new();

    public PassiveInventory(IOuiLookup? oui = null) => _oui = oui;

    /// <summary>Frames that turned into a sighting.</summary>
    public long Frames { get; private set; }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _devices.Count;
            }
        }
    }

    /// <summary>Folds one sighting in. Returns true when it was a device not heard before.</summary>
    public bool Observe(PassiveSighting sighting, DateTimeOffset utc)
    {
        ArgumentNullException.ThrowIfNull(sighting);

        lock (_gate)
        {
            Frames++;
            bool isNew = !_devices.TryGetValue(sighting.Mac, out Entry? entry);

            if (entry is null)
            {
                entry = new Entry(sighting.Mac, utc);
                _devices[sighting.Mac] = entry;
            }

            entry.Add(sighting, utc);
            return isNew;
        }
    }

    /// <summary>Every device, most recently heard first.</summary>
    public IReadOnlyList<PassiveDevice> Snapshot()
    {
        lock (_gate)
        {
            return [.. _devices.Values.Select(Freeze).OrderByDescending(d => d.LastHeardUtc).ThenBy(d => d.Mac.ToString(), StringComparer.Ordinal)];
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _devices.Clear();
            Frames = 0;
        }
    }

    private PassiveDevice Freeze(Entry entry) => new()
    {
        Mac = entry.Mac,
        Vendor = _oui?.Lookup(entry.Mac),
        Addresses = [.. entry.Stated, .. entry.Traffic.Where(a => !entry.Stated.Contains(a))],
        Names = [.. entry.Names],
        Sources = entry.Sources,
        Detail = entry.Detail,
        Vlan = entry.Vlan,
        FirstHeardUtc = entry.First,
        LastHeardUtc = entry.Last,
        Frames = entry.Frames,
    };

    private sealed class Entry(MacAddress mac, DateTimeOffset first)
    {
        public MacAddress Mac { get; } = mac;

        public DateTimeOffset First { get; } = first;

        public DateTimeOffset Last { get; private set; } = first;

        public List<IPAddress> Stated { get; } = [];

        public List<IPAddress> Traffic { get; } = [];

        public List<string> Names { get; } = [];

        public PassiveSource Sources { get; private set; }

        public string? Detail { get; private set; }

        public ushort? Vlan { get; private set; }

        public long Frames { get; private set; }

        public void Add(PassiveSighting sighting, DateTimeOffset utc)
        {
            Frames++;
            Last = utc > Last ? utc : Last;
            Sources |= sighting.Source;
            Vlan ??= sighting.Vlan;

            if (sighting.Address is { } address)
            {
                bool stated = sighting.Source is PassiveSource.Arp or PassiveSource.Dhcp or PassiveSource.ProfinetDcp
                    or PassiveSource.EnipIdentity or PassiveSource.Lldp;
                List<IPAddress> into = stated ? Stated : Traffic;

                if (!into.Contains(address) && into.Count < MaxAddresses)
                {
                    into.Add(address);
                }
            }

            if (!string.IsNullOrWhiteSpace(sighting.Name))
            {
                Names.Remove(sighting.Name);
                Names.Insert(0, sighting.Name);
                if (Names.Count > MaxNames)
                {
                    Names.RemoveAt(Names.Count - 1);
                }
            }

            // A conflict probe outranks everything else as the line to show; otherwise the newest wins.
            if (sighting.Detail is { } detail
                && (Detail is null || !Detail.StartsWith("probing", StringComparison.Ordinal) || detail.StartsWith("probing", StringComparison.Ordinal)))
            {
                Detail = detail;
            }
        }
    }
}
