using System.Net;
using NetControl.Core.Enip;
using NetControl.Core.Interfaces;
using NetControl.Core.Oui;

namespace NetControl.Core.Discovery;

/// <summary>
/// A scan, and then the two lookups that turn its replies into something the rest of the tool can
/// use: the ARP cache for the hardware address, and the IEEE registry for the vendor.
///
/// <para><b>Order matters and it is not an implementation detail.</b> The ARP read happens
/// immediately after the collection window closes, because the replies are what populated those
/// entries. Read it a minute later and the entries have started ageing out; read it before, and
/// they were never there. This is also why resolving a MAC costs nothing extra on the wire.</para>
///
/// <para><b>What this is for.</b> A device that answers ListIdentity already has an address, so
/// discovery is not how the plan gets filled - it is verification (the device we served came up
/// where we said), conflict detection (something else is already on that address), and inventory
/// (what is on this segment at all).</para>
/// </summary>
public sealed class DeviceDiscovery
{
    private readonly IIdentityScanner _scanner;
    private readonly IArpLookup _arp;
    private readonly IOuiLookup _oui;

    public DeviceDiscovery(IIdentityScanner scanner, IArpLookup arp, IOuiLookup oui)
    {
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(arp);
        ArgumentNullException.ThrowIfNull(oui);

        _scanner = scanner;
        _arp = arp;
        _oui = oui;
    }

    /// <summary>
    /// Scans from one adapter and resolves what answers.
    ///
    /// <para>Results come back when the window closes rather than one at a time. A scan is two
    /// seconds long and the ARP read that completes each row cannot happen until it is over, so a
    /// live-updating list would show every device twice: once nameless, once resolved.</para>
    /// </summary>
    public async Task<DiscoveryResult> ScanAsync(
        NicInfo nic,
        IReadOnlyList<IPAddress>? unicastTargets = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nic);

        ScanReport report = await _scanner.ScanAsync(nic, unicastTargets, cancellationToken).ConfigureAwait(false);

        return Resolve(nic, report);
    }

    /// <summary>
    /// Identifies only the addresses given, one unicast ListIdentity each, with no broadcast. This
    /// is how devices behind a router get a product name: a subnet sweep finds what answers ping,
    /// and this asks each of those what it is. Their MACs are out of reach - ARP stops at the
    /// router - and <see cref="MacSource.Routed"/> says so.
    /// </summary>
    public async Task<DiscoveryResult> IdentifyAsync(
        NicInfo nic,
        IReadOnlyList<IPAddress> targets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nic);
        ArgumentNullException.ThrowIfNull(targets);

        ScanReport report = await _scanner.ProbeAsync(nic, targets, cancellationToken).ConfigureAwait(false);

        return Resolve(nic, report);
    }

    private DiscoveryResult Resolve(NicInfo nic, ScanReport report)
    {
        if (report.Replies.Count == 0)
        {
            return new DiscoveryResult(report, []);
        }

        // "The table is empty" and "there is no table to read" are different answers, and the
        // second one is not the device's fault - so it gets its own wording rather than being
        // reported as a device that failed to appear in a cache nobody looked at.
        bool checkedArp = _arp.IsSupported;
        IReadOnlyList<ArpEntry> arp = checkedArp ? _arp.Snapshot() : [];

        HashSet<IPAddress> contested = ContestedAddresses(report.Replies);

        var devices = new List<DiscoveredDevice>(report.Replies.Count);

        foreach (ListIdentityReply reply in report.Replies)
        {
            (MacAddress mac, MacSource source) = checkedArp
                ? ResolveMac(reply, nic, arp)
                : (MacAddress.Empty, MacSource.NotChecked);

            devices.Add(new DiscoveredDevice
            {
                Identity = reply,
                Mac = mac,
                MacSource = source,

                // Resolved once, here, and afterwards carried as text - the same rule the plan
                // follows. The registry moves, and a record that re-resolves on every read is a
                // record that quietly changes its mind about who made a device.
                Vendor = mac.IsEmpty ? null : _oui.Lookup(mac),
                AddressIsContested = contested.Contains(reply.Address),
            });
        }

        return new DiscoveryResult(report, devices);
    }

    /// <summary>
    /// Addresses that more than one distinct device answered on. The scanner has already collapsed
    /// one device answering twice, so anything left sharing an address is genuinely two devices.
    /// </summary>
    private static HashSet<IPAddress> ContestedAddresses(IReadOnlyList<ListIdentityReply> replies)
    {
        var seen = new HashSet<IPAddress>();
        var contested = new HashSet<IPAddress>();

        foreach (ListIdentityReply reply in replies)
        {
            if (!seen.Add(reply.Address))
            {
                contested.Add(reply.Address);
            }
        }

        return contested;
    }

    /// <summary>
    /// The ARP entry is looked up against the address the reply <em>came from</em>, not the one the
    /// device claims. They are the same thing on a flat segment; when they differ, the sender is
    /// the one the cache was populated by and the claim is the one that is wrong.
    /// </summary>
    private static (MacAddress Mac, MacSource Source) ResolveMac(
        ListIdentityReply reply, NicInfo nic, IReadOnlyList<ArpEntry> arp)
    {
        // A sender on none of this adapter's subnets came through a router, and ARP stops there. An
        // entry this adapter did learn for it is still taken below - that is the cache saying the
        // address is on the wire after all - but its absence means "routed", not "missing".
        bool routed = Ipv4Subnet.TryCreate(nic.IPv4, nic.Mask, out _)
            && !nic.PerAddress().Any(n => Ipv4Subnet.TryCreate(n.IPv4, n.Mask, out Ipv4Subnet s)
                && s.Contains(reply.SenderAddress));

        if (arp.Count == 0)
        {
            return (MacAddress.Empty, routed ? MacSource.Routed : MacSource.NotInArp);
        }

        ArpEntry? onThisAdapter = null;
        ArpEntry? elsewhere = null;

        foreach (ArpEntry entry in arp)
        {
            if (!entry.IsUsable || !entry.Address.Equals(reply.SenderAddress))
            {
                continue;
            }

            if (entry.InterfaceIndex == nic.Index)
            {
                onThisAdapter = entry;
                break;
            }

            elsewhere ??= entry;
        }

        if (onThisAdapter is not null)
        {
            return (onThisAdapter.Mac, MacSource.Arp);
        }

        if (routed)
        {
            return (MacAddress.Empty, MacSource.Routed);
        }

        // Deliberately not taken. A hardware address learned on a different adapter is very
        // likely a different device that happens to share an address - and a plan row keyed on
        // the wrong MAC is a device that is never served and nobody can explain why.
        return elsewhere is not null
            ? (MacAddress.Empty, MacSource.ArpOnAnotherAdapter)
            : (MacAddress.Empty, MacSource.NotInArp);
    }
}
