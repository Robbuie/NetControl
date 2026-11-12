using System.Net;
using System.Net.Sockets;
using NetControl.Core.Diagnostics;
using NetControl.Core.Interfaces;

namespace NetControl.Core.Analysis;

/// <summary>
/// Watches which MAC claims which IPv4 address and reports any address claimed by two.
///
/// <para><b>Sends nothing.</b> It is fed observations: this PC's ARP table read on a timer (no
/// driver needed, but it only sees addresses this PC has talked to, and holds one MAC per address -
/// so a duplicate shows as the entry flipping between reads), and ARP frames heard through Npcap on
/// the Passive tab (sees everything broadcast on the segment, including the RFC 5227 probe a device
/// sends before taking an address).</para>
///
/// <para><b>Why this matters on a controls network.</b> Two devices on one address work
/// intermittently - each takes the traffic in turn as the switches and ARP caches flip - which looks
/// exactly like a bad cable or a flaky module, and is the fault a ping cannot find because the
/// address does answer. The first sign is usually an I/O connection dropping every few minutes.</para>
///
/// <para>Bounded: <see cref="MaxAddresses"/> addresses and <see cref="MaxMacsPerAddress"/> MACs each.</para>
/// </summary>
public sealed class AddressConflictDetector
{
    public const int MaxAddresses = 4096;

    public const int MaxMacsPerAddress = 4;

    /// <summary>How long a claim counts. Long enough to catch a duplicate that only flips every few minutes.</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(10);

    private readonly Dictionary<IPAddress, Record> _addresses = [];
    private readonly Dictionary<IPAddress, int> _reported = [];
    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private Dictionary<IPAddress, MacAddress> _own = [];

    public AddressConflictDetector(TimeSpan? window = null, TimeProvider? timeProvider = null)
    {
        Window = window ?? DefaultWindow;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Raised, on the observing thread, when an address becomes contested or gains another claimant.</summary>
    public event EventHandler<AddressConflict>? ConflictDetected;

    public TimeSpan Window { get; }

    /// <summary>Observations taken since start or <see cref="Clear"/>.</summary>
    public long Observations { get; private set; }

    public int AddressesTracked
    {
        get
        {
            lock (_gate)
            {
                return _addresses.Count;
            }
        }
    }

    /// <summary>This PC's own addresses and the MAC each lives on, so a claim to one by anything else is caught.</summary>
    public void SetOwnAddresses(IEnumerable<(IPAddress Address, MacAddress Mac)> own)
    {
        ArgumentNullException.ThrowIfNull(own);
        var map = new Dictionary<IPAddress, MacAddress>();

        foreach ((IPAddress address, MacAddress mac) in own)
        {
            if (address is not null)
            {
                map[address] = mac;
            }
        }

        lock (_gate)
        {
            _own = map;
        }
    }

    /// <summary>One sighting: <paramref name="mac"/> claims <paramref name="address"/> (or, for a probe, asks about it).</summary>
    public void Observe(IPAddress address, MacAddress mac, AddressObservationSource source, DateTimeOffset? utc = null)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (!IsHostAddress(address) || mac.IsEmpty || mac.IsMulticast)
        {
            return;
        }

        DateTimeOffset now = utc ?? _time.GetUtcNow();
        AddressConflict? raise = null;

        lock (_gate)
        {
            Observations++;

            if (!_addresses.TryGetValue(address, out Record? record))
            {
                if (_addresses.Count >= MaxAddresses && !PruneLocked(now))
                {
                    return;
                }

                record = new Record();
                _addresses[address] = record;
            }

            if (source == AddressObservationSource.Probe)
            {
                if (!record.Probes.Contains(mac) && record.Probes.Count < MaxMacsPerAddress)
                {
                    record.Probes.Add(mac);
                }

                record.LastProbeUtc = now;
            }
            else
            {
                Claim? claim = record.Claims.Find(c => c.Mac == mac);

                if (claim is null)
                {
                    if (record.Claims.Count >= MaxMacsPerAddress)
                    {
                        record.Claims.Sort((a, b) => a.Last.CompareTo(b.Last));
                        record.Claims.RemoveAt(0);
                    }

                    claim = new Claim(mac, now);
                    record.Claims.Add(claim);
                }

                claim.Last = now > claim.Last ? now : claim.Last;
                claim.Source = source;
                claim.Times++;
            }

            if (Evaluate(address, record, now) is { } conflict)
            {
                int weight = conflict.Claimants.Count + conflict.ProbedBy.Count;

                if (!_reported.TryGetValue(address, out int was) || weight > was)
                {
                    _reported[address] = weight;
                    raise = conflict;
                }
            }
        }

        if (raise is not null)
        {
            NetTrace.Log.Warn($"Address conflict: {raise.ToFinding().Title} - {raise.MacsText}.");

            try
            {
                ConflictDetected?.Invoke(this, raise);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                NetTrace.Log.Warn("A conflict handler failed.", ex);
            }
        }
    }

    /// <summary>
    /// Feeds one captured frame in. Only Ethernet/IPv4 ARP is looked at; anything else is ignored,
    /// so the Passive tab can hand over every frame it hears.
    /// </summary>
    public void ObserveFrame(ReadOnlySpan<byte> frame, DateTimeOffset utc)
    {
        if (!TryReadArp(frame, out IPAddress? sender, out MacAddress mac, out IPAddress? target) || sender is null || target is null)
        {
            return;
        }

        if (sender.Equals(IPAddress.Any))
        {
            // RFC 5227 probe: "is anybody using this address?" - asked before taking it.
            Observe(target, mac, AddressObservationSource.Probe, utc);
            return;
        }

        Observe(sender, mac, sender.Equals(target) ? AddressObservationSource.Gratuitous : AddressObservationSource.ArpFrame, utc);
    }

    /// <summary>The sender's address and MAC, and the address asked about, from an Ethernet/IPv4 ARP frame.</summary>
    internal static bool TryReadArp(ReadOnlySpan<byte> frame, out IPAddress? sender, out MacAddress mac, out IPAddress? target)
    {
        sender = null;
        target = null;
        mac = default;

        if (!Capture.EthernetFrame.TryRead(frame, out Capture.EthernetFrame ethernet) || ethernet.EtherType != Capture.EthernetFrame.Arp)
        {
            return false;
        }

        ReadOnlySpan<byte> arp = frame[ethernet.PayloadOffset..];
        if (arp.Length < 28 || arp[0] != 0 || arp[1] != 1 || arp[2] != 0x08 || arp[3] != 0x00 || arp[4] != 6 || arp[5] != 4)
        {
            return false;
        }

        mac = new MacAddress(arp[8..]);
        sender = new IPAddress(arp.Slice(14, 4));
        target = new IPAddress(arp.Slice(24, 4));
        return true;
    }

    /// <summary>
    /// Feeds this PC's ARP table in: every usable entry, or only those learned on
    /// <paramref name="interfaceIndex"/> when given - an entry learned on another adapter is likely
    /// a different device on a different network that happens to use the same numbers.
    /// </summary>
    public void ObserveArpTable(IEnumerable<ArpEntry> entries, int? interfaceIndex = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        DateTimeOffset now = _time.GetUtcNow();

        foreach (ArpEntry entry in entries)
        {
            if (entry.IsUsable && (interfaceIndex is null || entry.InterfaceIndex == interfaceIndex))
            {
                Observe(entry.Address, entry.Mac, AddressObservationSource.ArpTable, now);
            }
        }
    }

    /// <summary>Every address currently contested within the window, own address first, then by address.</summary>
    public IReadOnlyList<AddressConflict> Conflicts()
    {
        DateTimeOffset now = _time.GetUtcNow();

        lock (_gate)
        {
            var list = new List<AddressConflict>();

            foreach ((IPAddress address, Record record) in _addresses)
            {
                if (Evaluate(address, record, now) is { } conflict)
                {
                    list.Add(conflict);
                }
            }

            return [.. list
                .OrderByDescending(c => c.IsOwnAddress)
                .ThenBy(c => SortKey(c.Address))];
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _addresses.Clear();
            _reported.Clear();
            Observations = 0;
        }
    }

    private AddressConflict? Evaluate(IPAddress address, Record record, DateTimeOffset now)
    {
        DateTimeOffset since = now - Window;
        bool own = _own.TryGetValue(address, out MacAddress ownMac);

        List<Claim> live = [.. record.Claims.Where(c => c.Last >= since && !(own && c.Mac == ownMac))];
        List<MacAddress> probes = record.LastProbeUtc >= since
            ? [.. record.Probes.Where(p => !live.Any(c => c.Mac == p) && !(own && p == ownMac))]
            : [];

        bool contested = own
            ? live.Count > 0
            : live.Count >= 2 || (live.Count == 1 && probes.Count > 0);

        if (!contested)
        {
            return null;
        }

        return new AddressConflict(
            address,
            [.. live.OrderBy(c => c.First).Select(c => new AddressClaim(c.Mac, c.First, c.Last, c.Source, c.Times))],
            own,
            probes);
    }

    /// <summary>Drops addresses with nothing inside the window. True when room was made.</summary>
    private bool PruneLocked(DateTimeOffset now)
    {
        DateTimeOffset since = now - Window;
        List<IPAddress> stale = [.. _addresses
            .Where(kv => kv.Value.Claims.All(c => c.Last < since) && kv.Value.LastProbeUtc < since)
            .Select(kv => kv.Key)];

        foreach (IPAddress address in stale)
        {
            _addresses.Remove(address);
            _reported.Remove(address);
        }

        return _addresses.Count < MaxAddresses;
    }

    private static bool IsHostAddress(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork
        && !address.Equals(IPAddress.Any)
        && !address.Equals(IPAddress.Broadcast)
        && address.GetAddressBytes() is [not (>= 224 and <= 239) and not 127, ..];

    private static uint SortKey(IPAddress address)
    {
        byte[] b = address.GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }

    private sealed class Record
    {
        public List<Claim> Claims { get; } = [];

        public List<MacAddress> Probes { get; } = [];

        public DateTimeOffset LastProbeUtc { get; set; } = DateTimeOffset.MinValue;
    }

    private sealed class Claim(MacAddress mac, DateTimeOffset first)
    {
        public MacAddress Mac { get; } = mac;

        public DateTimeOffset First { get; } = first;

        public DateTimeOffset Last { get; set; } = first;

        public AddressObservationSource Source { get; set; }

        public long Times { get; set; }
    }
}
