using System.Diagnostics;
using System.Net;
using NetControl.Core.Interfaces;
using NetControl.Core.Oui;
using NetControl.Core.Reachability;

namespace NetControl.Core.Discovery;

/// <summary>
/// Finds everything on the selected adapter's subnet, not only what speaks EtherNet/IP: one echo
/// request to every host address, then a read of this PC's ARP cache.
///
/// <para><b>The ARP read is what makes this worth more than a ping.</b> Before Windows can send an
/// echo to an address on its own subnet it has to ARP for it, and a device answers ARP whether or
/// not it is set to ignore ping - which a good share of drives and I/O adapters are. So the cache
/// afterwards holds the devices that answered ping <em>and</em> the ones that stayed silent, each
/// with its MAC, at the cost of nothing extra on the wire. That is how an office IP scanner finds
/// the devices a ping alone cannot.</para>
///
/// <para><b>Gentle, and bounded.</b> The pinging is <see cref="PingSweep"/>, so every address passes
/// <see cref="UnicastTarget"/> first and the concurrency is capped; the defaults are in
/// <see cref="SubnetSweepOptions"/>. A subnet wider than <see cref="SubnetSweepOptions.MaxAddresses"/>
/// is refused before anything is sent, because a /16 is a half-hour scan that nobody meant to start
/// with one click. Nothing here opens a connection or writes to anything.</para>
///
/// <para><b>Only the adapter's own subnet.</b> ARP does not cross a router, so a sweep of somebody
/// else's subnet would lose the half of this that matters. The range is computed from the adapter's
/// address and mask, never typed.</para>
/// </summary>
public sealed class SubnetSweep
{
    private readonly IPinger _pinger;
    private readonly IArpLookup _arp;
    private readonly IOuiLookup _oui;

    public SubnetSweep(IPinger pinger, IArpLookup arp, IOuiLookup oui, SubnetSweepOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(pinger);
        ArgumentNullException.ThrowIfNull(arp);
        ArgumentNullException.ThrowIfNull(oui);

        _pinger = pinger;
        _arp = arp;
        _oui = oui;
        Options = options ?? new SubnetSweepOptions();
    }

    public SubnetSweepOptions Options { get; }

    /// <summary>
    /// One address has its answer. Raised on whatever thread the ping completed on, so a UI marshals
    /// - the same rule as every other Core event.
    /// </summary>
    public event EventHandler<PingProgressEventArgs>? Progress;

    /// <summary>
    /// The subnet a sweep from <paramref name="nic"/> would cover and how many addresses that is, or
    /// why it would be refused. Sends nothing; this is what a button's caption is made from.
    /// </summary>
    public bool TryPlan(NicInfo? nic, out Ipv4Subnet subnet, out int addresses, out string? problem)
    {
        subnet = default;
        addresses = 0;
        problem = null;

        if (nic is null)
        {
            problem = "No adapter is selected. A sweep goes out of exactly one adapter, and it is never chosen for you.";
            return false;
        }

        if (!nic.CanServe)
        {
            problem = $"[{nic.Index}] {nic.Name} cannot be swept from: "
                + (nic.IsUp ? (nic.IsApipa ? "it has only an APIPA address." : "it has no IPv4 address.") : "it is down.");
            return false;
        }

        if (!Ipv4Subnet.TryCreate(nic.IPv4, nic.Mask, out subnet))
        {
            problem = $"[{nic.Index}] {nic.Name} has no usable subnet mask, so there is no subnet to sweep.";
            return false;
        }

        if (subnet.PrefixLength >= 31)
        {
            problem = $"{subnet} is a point-to-point link with no neighbours to sweep.";
            return false;
        }

        // Every host address except this PC's own.
        long hosts = subnet.HostCount - 1;

        if (hosts > Options.MaxAddresses)
        {
            problem = $"{subnet} holds {hosts:N0} addresses, more than the {Options.MaxAddresses:N0} a sweep will "
                + "cover. Industrial segments are normally a /24; a mask this wide is usually the office network, "
                + "not the panel.";
            return false;
        }

        addresses = (int)hosts;
        return true;
    }

    /// <summary>Sweeps the subnet <paramref name="nic"/>'s address is on.</summary>
    /// <exception cref="ReachabilityException">The sweep was refused. Nothing was sent.</exception>
    public Task<SubnetSweepResult> SweepAsync(NicInfo nic, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nic);

        if (!TryPlan(nic, out Ipv4Subnet subnet, out _, out string? problem))
        {
            throw new ReachabilityException(problem!)
            {
                Remediation = "Select the adapter that is plugged into the panel's network, with an address and "
                    + "mask on that network.",
            };
        }

        List<PingTarget> targets = [.. Hosts(subnet)
            .Where(a => !nic.Owns(a))
            .Select(a => new PingTarget(a, subnet.Mask))];

        return RunAsync(nic, subnet.ToString(), targets, subnet.Contains, subnet.Mask, isRouted: false, cancellationToken);
    }

    /// <summary>
    /// Sweeps a range somebody typed, which may be behind a router. Same pinging, same pace, same
    /// one-host check on every address; what changes is what can be learned. ARP stops at a router,
    /// so a routed host has no MAC and a routed device that ignores ping is not found at all - and
    /// the result says it was routed so the screen and the record can say so too.
    /// </summary>
    /// <exception cref="ReachabilityException">The sweep was refused. Nothing was sent.</exception>
    public Task<SubnetSweepResult> SweepRangeAsync(
        NicInfo nic, SweepRange range, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nic);
        ArgumentNullException.ThrowIfNull(range);

        if (!nic.CanServe)
        {
            throw new ReachabilityException($"[{nic.Index}] {nic.Name} cannot source a sweep: "
                + (nic.IsUp ? "it has no usable IPv4 address." : "it is down."))
            {
                Remediation = "Select an adapter with an address - the one that has a route to that range.",
            };
        }

        if (range.Count > Options.MaxAddresses)
        {
            throw new ReachabilityException(
                $"{range} is {range.Count:N0} addresses, more than the {Options.MaxAddresses:N0} a sweep will cover.")
            {
                Remediation = "Split it into smaller ranges, or narrow it to the part of the plant you mean.",
            };
        }

        bool onLink = nic.PerAddress().Any(n => Ipv4Subnet.TryCreate(n.IPv4, n.Mask, out Ipv4Subnet s) && range.IsWithin(s));

        List<PingTarget> targets = [.. range.Addresses()
            .Where(a => !nic.Owns(a))
            .Select(a => new PingTarget(a, range.Mask))];

        return RunAsync(nic, range.Text, targets, range.Contains, range.Mask, isRouted: !onLink, cancellationToken);
    }

    private async Task<SubnetSweepResult> RunAsync(
        NicInfo nic,
        string scope,
        List<PingTarget> targets,
        Func<IPAddress, bool> inScope,
        IPAddress? mask,
        bool isRouted,
        CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();

        var sweep = new PingSweep(_pinger, Options.ToPingOptions());
        sweep.Progress += (_, e) => Progress?.Invoke(this, e);

        PingSweepResult pinged = await sweep.RunAsync(targets, cancellationToken).ConfigureAwait(false);

        if (Options.ArpSettle > TimeSpan.Zero)
        {
            await Task.Delay(Options.ArpSettle, cancellationToken).ConfigureAwait(false);
        }

        bool arpChecked = _arp.IsSupported;
        IReadOnlyList<ArpEntry> arp = arpChecked ? _arp.Snapshot() : [];

        IReadOnlyList<SweptHost> hosts = Join(nic, inScope, mask, pinged.Outcomes, arp, arpChecked);

        return new SubnetSweepResult(nic, scope, hosts, targets.Count, arpChecked, Stopwatch.GetElapsedTime(started))
        {
            IsRouted = isRouted,
        };
    }

    /// <summary>Every host address on the subnet, in order.</summary>
    private static IEnumerable<IPAddress> Hosts(Ipv4Subnet subnet)
    {
        uint first = ToUInt32(subnet.FirstHost);
        uint last = ToUInt32(subnet.LastHost);

        for (uint bits = first; bits <= last && bits >= first; bits++)
        {
            yield return FromUInt32(bits);
        }
    }

    /// <summary>
    /// The pings and the ARP cache, joined by address. An address is in the result if it answered
    /// ping, or if the cache holds it on this adapter; silence with no ARP entry is not a host.
    /// </summary>
    internal IReadOnlyList<SweptHost> Join(
        NicInfo nic,
        Func<IPAddress, bool> inScope,
        IPAddress? mask,
        IReadOnlyList<PingOutcome> outcomes,
        IReadOnlyList<ArpEntry> arp,
        bool arpChecked)
    {
        var onThisAdapter = new Dictionary<IPAddress, ArpEntry>();
        var elsewhere = new HashSet<IPAddress>();

        foreach (ArpEntry entry in arp)
        {
            if (!entry.IsUsable)
            {
                continue;
            }

            if (entry.InterfaceIndex == nic.Index)
            {
                onThisAdapter.TryAdd(entry.Address, entry);
            }
            else
            {
                elsewhere.Add(entry.Address);
            }
        }

        // The subnets this adapter is on. An address outside all of them is reached through a
        // router, and ARP - so the MAC - stops at the router.
        List<Ipv4Subnet> onLink = [.. nic.PerAddress()
            .Select(n => Ipv4Subnet.TryCreate(n.IPv4, n.Mask, out Ipv4Subnet s) ? s : (Ipv4Subnet?)null)
            .OfType<Ipv4Subnet>()];

        var byAddress = new Dictionary<IPAddress, SweptHost>();

        foreach (PingOutcome outcome in outcomes)
        {
            // Outcomes is filled by index, and a cancelled sweep never gets here - but a null slot
            // would be a ping that never finished, and it is not evidence of anything.
            if (outcome is null)
            {
                continue;
            }

            bool inArp = onThisAdapter.TryGetValue(outcome.Address, out ArpEntry? entry);

            if (!outcome.Replied && !inArp)
            {
                continue;
            }

            byAddress[outcome.Address] = Host(outcome.Address, outcome, entry, elsewhere, arpChecked, onLink);
        }

        // Entries in scope that were not in the outcomes - none, normally, since every address was
        // pinged; but a sweep is cheap to make honest about what the cache holds.
        foreach ((IPAddress address, ArpEntry entry) in onThisAdapter)
        {
            if (byAddress.ContainsKey(address)
                || nic.Owns(address)
                || !inScope(address)
                || !UnicastTarget.TryCheck(address, mask, out _))
            {
                continue;
            }

            byAddress[address] = Host(address, ping: null, entry, elsewhere, arpChecked, onLink);
        }

        return [.. byAddress.Values.OrderBy(h => ToUInt32(h.Address))];
    }

    private SweptHost Host(
        IPAddress address,
        PingOutcome? ping,
        ArpEntry? entry,
        HashSet<IPAddress> elsewhere,
        bool arpChecked,
        List<Ipv4Subnet> onLink)
    {
        MacSource source = entry is not null ? MacSource.Arp
            : !onLink.Any(s => s.Contains(address)) ? MacSource.Routed
            : !arpChecked ? MacSource.NotChecked
            : elsewhere.Contains(address) ? MacSource.ArpOnAnotherAdapter
            : MacSource.NotInArp;

        MacAddress mac = source == MacSource.Arp ? entry!.Mac : MacAddress.Empty;

        return new SweptHost
        {
            Address = address,
            Ping = ping,
            InArp = entry is not null,
            Mac = mac,
            MacSource = source,
            Vendor = mac.IsEmpty ? null : _oui.Lookup(mac),
        };
    }

    private static uint ToUInt32(IPAddress address)
    {
        byte[] b = address.GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }

    private static IPAddress FromUInt32(uint bits) =>
        new([(byte)(bits >> 24), (byte)(bits >> 16), (byte)(bits >> 8), (byte)bits]);
}
