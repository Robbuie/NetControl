using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using NetControl.Core.Dhcp;
using NetControl.Core.Oui;

namespace NetControl.Core.Analysis;

/// <summary>
/// Counts what the Passive tab hears and says what it means: broadcast storms, a device shouting
/// over everybody, spanning-tree topology changes, another BOOTP/DHCP server answering, a device
/// stuck asking for an address.
///
/// <para><b>Sends nothing</b> - it reads bytes handed to it. It sees what the capture filter lets
/// through: broadcast, multicast, ARP, LLDP, PROFINET, spanning tree, and BOOTP/DHCP and
/// EtherNet/IP unicast. On a switched network that is also everything this PC's port receives
/// anyway, which is exactly the traffic that loads every device's CPU.</para>
///
/// <para><b>Fixed cost.</b> Per frame it does a header read and a dictionary update; the history
/// is a fixed ring of seconds and the source table is capped, so a flood costs CPU in proportion to
/// the frames and memory not at all.</para>
/// </summary>
public sealed class TrafficMonitor
{
    /// <summary>Sources tracked individually. Past this, new MACs are counted but not named.</summary>
    public const int MaxSources = 4096;

    /// <summary>Seconds of history kept for the sparkline.</summary>
    public const int HistorySeconds = 120;

    private const int AverageSeconds = 10;
    private const double Decay = 0.9;

    private static readonly MacAddress StpMulticast = MacAddress.Parse("01:80:C2:00:00:00");

    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private readonly IOuiLookup? _oui;
    private readonly Dictionary<MacAddress, Source> _sources = [];
    private readonly Dictionary<IPAddress, DhcpServerSighting> _servers = [];
    private readonly Bucket[] _history = new Bucket[HistorySeconds];
    private HashSet<MacAddress> _ownMacs = [];

    private Bucket _current;
    private long _currentSecond = long.MinValue;
    private int _historyNext;
    private int _historyCount;
    private Bucket _total;
    private long _peakBroadcast;
    private long _untracked;
    private DateTimeOffset? _started;
    private int _topologyChanges;
    private DateTimeOffset? _lastTopologyChange;
    private DateTimeOffset? _lastTcBpdu;
    private string? _root;
    private int _rootChanges;

    public TrafficMonitor(TrafficThresholds? thresholds = null, IOuiLookup? oui = null, TimeProvider? timeProvider = null)
    {
        Thresholds = thresholds ?? new TrafficThresholds();
        _oui = oui;
        _time = timeProvider ?? TimeProvider.System;
    }

    public TrafficThresholds Thresholds { get; }

    /// <summary>This PC's MACs, so its own replies are not reported as another DHCP server.</summary>
    public void SetOwnMacs(IEnumerable<MacAddress> macs)
    {
        ArgumentNullException.ThrowIfNull(macs);
        HashSet<MacAddress> set = [.. macs];

        lock (_gate)
        {
            _ownMacs = set;
        }
    }

    /// <summary>Counts one frame. Called on the capture thread.</summary>
    public void Observe(ReadOnlySpan<byte> frame, DateTimeOffset utc)
    {
        if (frame.Length < 14)
        {
            return;
        }

        var destination = new MacAddress(frame);
        var sourceMac = new MacAddress(frame[6..]);
        ushort typeOrLength = BinaryPrimitives.ReadUInt16BigEndian(frame[12..]);
        int offset = 14;

        if (typeOrLength == 0x8100 && frame.Length >= 18)
        {
            typeOrLength = BinaryPrimitives.ReadUInt16BigEndian(frame[16..]);
            offset = 18;
        }

        lock (_gate)
        {
            _started ??= utc;
            RollTo(utc);

            _current.Frames++;
            _current.Bytes += frame.Length;

            if (destination.IsBroadcast)
            {
                _current.Broadcast++;
            }
            else if (destination.IsMulticast)
            {
                _current.Multicast++;
            }

            Source? source = Track(sourceMac);

            if (typeOrLength == 0x0806)
            {
                _current.Arp++;
            }
            else if (typeOrLength == 0x88CC)
            {
                _current.Lldp++;
            }
            else if (typeOrLength == 0x0800)
            {
                ObserveIPv4(frame[offset..], sourceMac, source, utc);
            }
            else if (typeOrLength < 0x0600 && destination == StpMulticast)
            {
                _current.Stp++;
                ObserveBpdu(frame[offset..], utc);
            }
        }
    }

    public TrafficSnapshot Snapshot()
    {
        lock (_gate)
        {
            DateTimeOffset now = _time.GetUtcNow();
            if (_started is null)
            {
                return TrafficSnapshot.Empty;
            }

            RollTo(now);

            List<Bucket> recent = LastSeconds(AverageSeconds);
            double seconds = Math.Max(1, recent.Count);
            double broadcast = recent.Sum(b => b.Broadcast) / seconds;
            double multicast = recent.Sum(b => b.Multicast) / seconds;
            double arp = recent.Sum(b => b.Arp) / seconds;
            double frames = recent.Sum(b => b.Frames) / seconds;

            List<TrafficTalker> talkers = [.. _sources
                .OrderByDescending(kv => kv.Value.Recent)
                .Take(10)
                .Where(kv => kv.Value.Recent > 0)
                .Select(kv => new TrafficTalker(
                    kv.Key,
                    kv.Value.Address,
                    kv.Value.Recent * (1 - Decay),
                    kv.Value.Total,
                    _oui?.Lookup(kv.Key)))];

            var snapshot = new TrafficSnapshot
            {
                Seconds = (now - _started.Value).TotalSeconds,
                Frames = _total.Frames + _current.Frames,
                Bytes = _total.Bytes + _current.Bytes,
                Broadcast = _total.Broadcast + _current.Broadcast,
                Multicast = _total.Multicast + _current.Multicast,
                Arp = _total.Arp + _current.Arp,
                BootpRequests = _total.BootpRequests + _current.BootpRequests,
                BootpReplies = _total.BootpReplies + _current.BootpReplies,
                Stp = _total.Stp + _current.Stp,
                Lldp = _total.Lldp + _current.Lldp,
                BroadcastPerSecond = broadcast,
                MulticastPerSecond = multicast,
                ArpPerSecond = arp,
                FramesPerSecond = frames,
                PeakBroadcastPerSecond = _peakBroadcast,
                History = [.. LastSeconds(HistorySeconds).Select(b => b.Broadcast + b.Multicast)],
                TopTalkers = talkers,
                OtherDhcpServers = [.. _servers.Values.OrderByDescending(s => s.LastUtc)],
                TopologyChanges = _topologyChanges,
                LastTopologyChangeUtc = _lastTopologyChange,
                RootBridge = _root,
                RootBridgeChanges = _rootChanges,
                UntrackedSources = _untracked,
            };

            return snapshot with { Findings = Assess(snapshot, now) };
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _sources.Clear();
            _servers.Clear();
            Array.Clear(_history);
            _historyNext = 0;
            _historyCount = 0;
            _current = default;
            _total = default;
            _currentSecond = long.MinValue;
            _peakBroadcast = 0;
            _untracked = 0;
            _started = null;
            _topologyChanges = 0;
            _lastTopologyChange = null;
            _lastTcBpdu = null;
            _root = null;
            _rootChanges = 0;
        }
    }

    private List<NetworkFinding> Assess(TrafficSnapshot s, DateTimeOffset now)
    {
        var findings = new List<NetworkFinding>();
        TrafficThresholds t = Thresholds;

        // Too early to call anything: a single burst in the first second is not a rate.
        if (s.Seconds < 3)
        {
            return findings;
        }

        if (s.BroadcastPerSecond >= t.BroadcastStormPerSecond)
        {
            findings.Add(new NetworkFinding(
                FindingSeverity.Error,
                "Traffic",
                "Broadcast storm",
                Fmt($"{s.BroadcastPerSecond:0} broadcasts a second over the last ten seconds (peak {s.PeakBroadcastPerSecond:N0} in one second). "
                    + "Every device on the segment has to read every one of them."),
                "The usual cause is a loop - a cable patched between two switch ports, or two switches joined twice without "
                    + "spanning tree. Check the top talkers below and any cable added recently; unplug until it stops."));
        }
        else if (s.BroadcastPerSecond >= t.BroadcastWarnPerSecond)
        {
            findings.Add(new NetworkFinding(
                FindingSeverity.Warn,
                "Traffic",
                "High broadcast rate",
                Fmt($"{s.BroadcastPerSecond:0} broadcasts a second over the last ten seconds. A healthy controls segment is usually tens."),
                "Look at the top talkers: one device sending most of it is usually misconfigured or in a loop of its own."));
        }

        if (s.ArpPerSecond >= t.ArpWarnPerSecond)
        {
            findings.Add(new NetworkFinding(
                FindingSeverity.Warn,
                "Traffic",
                "A lot of ARP",
                Fmt($"{s.ArpPerSecond:0} ARP frames a second."),
                "Something is scanning the subnet, or a device is ARPing for an address that never answers. The top talker is the place to start."));
        }

        double shared = s.BroadcastPerSecond + s.MulticastPerSecond;
        if (s.TopTalkers.FirstOrDefault() is { } top
            && top.PerSecond >= t.TalkerMinimumPerSecond
            && shared > 0
            && top.PerSecond / Math.Max(shared, top.PerSecond) >= t.TalkerShare)
        {
            findings.Add(new NetworkFinding(
                FindingSeverity.Warn,
                "Traffic",
                $"One device is sending most of the traffic: {top.Mac}",
                Fmt($"{top.PerSecond:0} frames a second{(top.Address is null ? string.Empty : $" from {top.Address}")}"
                    + $"{(top.Vendor is null ? string.Empty : $" ({top.Vendor})")} - {top.PerSecond / Math.Max(shared, top.PerSecond):P0} of what was heard."),
                "If it is an EtherNet/IP adapter producing multicast I/O, that may be normal - check its RPI and whether its "
                    + "connections should be unicast. Otherwise power-cycle it or unplug it and see whether the rate drops."));
        }

        foreach (DhcpServerSighting server in s.OtherDhcpServers)
        {
            findings.Add(new NetworkFinding(
                FindingSeverity.Warn,
                "Addresses",
                $"Another BOOTP/DHCP server is answering: {server.Address}",
                Fmt($"{server.Replies} replies from {server.Address} ({server.Mac})")
                    + (server.LastOffered is { } offered ? $", last handed out {offered}" + (server.LastClient is { } client ? $" to {client}" : string.Empty) : string.Empty)
                    + ". A device takes the first answer it gets, so it may take that server's address instead of the planned one.",
                "Find the other server (a PC with a DHCP tool open, a router or a managed switch) and stop it, or make sure "
                    + "it does not answer these devices' MACs."));
        }

        if (s.TopologyChanges > 0 && s.LastTopologyChangeUtc is { } last)
        {
            bool recent = now - last < TimeSpan.FromMinutes(5);
            findings.Add(new NetworkFinding(
                recent ? FindingSeverity.Warn : FindingSeverity.Info,
                "Switches",
                Fmt($"Spanning tree reorganised {s.TopologyChanges} time(s)"),
                Fmt($"Last at {last.ToLocalTime():HH:mm:ss}. While spanning tree reconverges the switches flush their tables and "
                    + "traffic floods or stops for seconds - long enough to drop an I/O connection."),
                "A port going up and down does this: a loose cable, a device rebooting, a PC docking. Repeated changes point at a "
                    + "link that is flapping; check the switch's port log."));
        }

        if (s.RootBridgeChanges > 0)
        {
            findings.Add(new NetworkFinding(
                FindingSeverity.Warn,
                "Switches",
                "The spanning-tree root bridge changed",
                $"Now {s.RootBridge}. A new root means every switch recalculated its paths.",
                "Usually a switch was added or restarted with a better (lower) bridge priority. The root should be a core switch "
                    + "with its priority set deliberately."));
        }

        foreach ((MacAddress mac, Source src) in _sources)
        {
            if (src.BootpRecent * (1 - Decay) >= t.BootpFloodPerSecond)
            {
                findings.Add(new NetworkFinding(
                    FindingSeverity.Warn,
                    "Addresses",
                    $"{mac} keeps asking for an address",
                    Fmt($"About {src.BootpRecent * (1 - Decay):0} BOOTP/DHCP requests a second."),
                    "It is not getting an answer it accepts. Plan it (Live requests), or check whether it is rebooting in a loop."));
            }
        }

        if (findings.Count == 0)
        {
            findings.Add(new NetworkFinding(
                FindingSeverity.Ok,
                "Traffic",
                "Nothing unusual heard",
                Fmt($"{s.BroadcastPerSecond:0} broadcasts and {s.MulticastPerSecond:0} multicasts a second, no storms, no other "
                    + $"DHCP server, {(s.Stp > 0 ? "spanning tree steady" : "no spanning tree heard")}.")));
        }

        return [.. findings.OrderByDescending(f => f.Severity)];
    }

    private void ObserveIPv4(ReadOnlySpan<byte> ip, MacAddress sourceMac, Source? source, DateTimeOffset utc)
    {
        if (ip.Length < 20 || (ip[0] >> 4) != 4)
        {
            return;
        }

        int header = (ip[0] & 0x0F) * 4;
        var from = new IPAddress(ip.Slice(12, 4));

        if (source is not null && !from.Equals(IPAddress.Any))
        {
            source.Address = from;
        }

        if (ip[9] != 17 || ip.Length < header + 8)
        {
            return;
        }

        ReadOnlySpan<byte> udp = ip[header..];
        ushort sourcePort = BinaryPrimitives.ReadUInt16BigEndian(udp);
        ushort destinationPort = BinaryPrimitives.ReadUInt16BigEndian(udp[2..]);

        if (sourcePort == 68 && destinationPort == 67)
        {
            _current.BootpRequests++;
            if (source is not null)
            {
                source.BootpThisSecond++;
            }
        }
        else if (sourcePort == 67 && (destinationPort == 68 || destinationPort == 67))
        {
            _current.BootpReplies++;

            if (_ownMacs.Contains(sourceMac) || !BootpPacket.TryParse(udp[8..], out BootpPacket? reply) || reply is null || reply.IsRequest)
            {
                return;
            }

            IPAddress server = from.Equals(IPAddress.Any) ? reply.ServerIp : from;
            _servers.TryGetValue(server, out DhcpServerSighting? was);

            if (was is null && _servers.Count >= 64)
            {
                return;
            }

            _servers[server] = new DhcpServerSighting(
                server,
                sourceMac,
                (was?.Replies ?? 0) + 1,
                utc,
                reply.YourIp.Equals(IPAddress.Any) ? was?.LastOffered : reply.YourIp,
                reply.ClientMac.IsEmpty ? was?.LastClient : reply.ClientMac);
        }
    }

    private void ObserveBpdu(ReadOnlySpan<byte> llc, DateTimeOffset utc)
    {
        // LLC 42 42 03, then the BPDU: protocol id (2), version (1), type (1), flags (1), root id (8)...
        if (llc.Length < 7 || llc[0] != 0x42 || llc[1] != 0x42 || llc[2] != 0x03)
        {
            return;
        }

        ReadOnlySpan<byte> bpdu = llc[3..];
        byte type = bpdu[3];
        bool topologyChange = type == 0x80 || (bpdu[4] & 0x01) != 0;

        if (type != 0x80 && bpdu.Length >= 13)
        {
            ushort priority = BinaryPrimitives.ReadUInt16BigEndian(bpdu[5..]);
            var mac = new MacAddress(bpdu[7..]);
            string root = string.Create(CultureInfo.InvariantCulture, $"{priority}/{mac}");

            if (_root is not null && _root != root)
            {
                _rootChanges++;
            }

            _root = root;
        }

        if (!topologyChange)
        {
            return;
        }

        // Every bridge repeats the TC flag for most of a minute after one change, so a change is
        // a new event only after a quiet spell.
        if (_lastTcBpdu is null || utc - _lastTcBpdu.Value > Thresholds.TopologyChangeQuiet)
        {
            _topologyChanges++;
            _lastTopologyChange = utc;
        }

        _lastTcBpdu = utc;
    }

    private Source? Track(MacAddress mac)
    {
        if (mac.IsEmpty || mac.IsMulticast)
        {
            return null;
        }

        if (!_sources.TryGetValue(mac, out Source? source))
        {
            if (_sources.Count >= MaxSources)
            {
                _untracked++;
                return null;
            }

            source = new Source();
            _sources[mac] = source;
        }

        source.Total++;
        source.ThisSecond++;
        return source;
    }

    /// <summary>Closes the current second (and any silent seconds since) into the history.</summary>
    private void RollTo(DateTimeOffset utc)
    {
        long second = utc.ToUnixTimeSeconds();

        if (_currentSecond == long.MinValue)
        {
            _currentSecond = second;
            return;
        }

        if (second <= _currentSecond)
        {
            return;
        }

        long gap = Math.Min(second - _currentSecond, HistorySeconds + 1);

        for (long i = 0; i < gap; i++)
        {
            Push(i == 0 ? _current : default);
        }

        _current = default;
        _currentSecond = second;
    }

    private void Push(Bucket bucket)
    {
        _history[_historyNext] = bucket;
        _historyNext = (_historyNext + 1) % HistorySeconds;
        _historyCount = Math.Min(_historyCount + 1, HistorySeconds);
        _peakBroadcast = Math.Max(_peakBroadcast, bucket.Broadcast);

        _total.Frames += bucket.Frames;
        _total.Bytes += bucket.Bytes;
        _total.Broadcast += bucket.Broadcast;
        _total.Multicast += bucket.Multicast;
        _total.Arp += bucket.Arp;
        _total.BootpRequests += bucket.BootpRequests;
        _total.BootpReplies += bucket.BootpReplies;
        _total.Stp += bucket.Stp;
        _total.Lldp += bucket.Lldp;

        // One decay step per second for every source - a walk of at most MaxSources, once a second.
        foreach (Source source in _sources.Values)
        {
            source.Recent = (source.Recent * Decay) + source.ThisSecond;
            source.BootpRecent = (source.BootpRecent * Decay) + source.BootpThisSecond;
            source.ThisSecond = 0;
            source.BootpThisSecond = 0;
        }
    }

    private List<Bucket> LastSeconds(int count)
    {
        int n = Math.Min(count, _historyCount);
        var list = new List<Bucket>(n);
        int start = (_historyNext - n + HistorySeconds) % HistorySeconds;

        for (int i = 0; i < n; i++)
        {
            list.Add(_history[(start + i) % HistorySeconds]);
        }

        return list;
    }

    // Core runs under InvariantGlobalization, so interpolated numbers are already culture-invariant.
    private static string Fmt(string text) => text;

    private struct Bucket
    {
        public long Frames;
        public long Bytes;
        public long Broadcast;
        public long Multicast;
        public long Arp;
        public long BootpRequests;
        public long BootpReplies;
        public long Stp;
        public long Lldp;
    }

    private sealed class Source
    {
        public long Total { get; set; }

        public long ThisSecond { get; set; }

        public double Recent { get; set; }

        public long BootpThisSecond { get; set; }

        public double BootpRecent { get; set; }

        public IPAddress? Address { get; set; }
    }
}
