using NetControl.Core.Reachability;

namespace NetControl.Core.Discovery;

/// <summary>
/// How hard a subnet sweep leans on the segment. Gentle by default - see the scanning rule in
/// CLAUDE.md - and options rather than constants so a bench can try others.
///
/// <para>The defaults start at most fifty echo requests a second, one per address, so the segment
/// sees ARP broadcasts at about that rate (Windows may repeat one for an address nobody answers)
/// and each device that is there receives one ARP request and one echo. ICMP echo
/// is the cheapest thing a device can be asked - it is answered by the stack, not the application,
/// and opens nothing - which is why a sweep may cover a whole subnet while the plan's ping is kept
/// to a handful. There is no TCP in it at all; the port scans that knock over thin stacks are
/// something this never does.</para>
/// </summary>
public sealed record SubnetSweepOptions
{
    /// <summary>
    /// The widest subnet a sweep will cover. A /22 by default: 1,022 addresses, about half a minute
    /// at the default pace. A /16 would be 65,534 echo requests and the best part of half an
    /// hour, which is not a scan anybody meant to start by pressing a button.
    /// </summary>
    public int MaxAddresses { get; init; } = 1024;

    /// <summary>
    /// How long to wait for each echo reply. One second: on the adapter's own subnet a reply that
    /// is coming arrives in milliseconds.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Echo requests per address. One, unlike the plan's ping: a device whose first echo is lost
    /// while ARP resolves still lands in the ARP cache, and the sweep reads that cache afterwards -
    /// so a second echo would double the traffic to learn nothing the cache has not already said.
    /// </summary>
    public int Attempts { get; init; } = 1;

    /// <summary>
    /// Addresses being pinged at once. Most addresses on a subnet are empty and hold their slot for
    /// the whole timeout, so this is what sets how long a sweep takes; the pace below is what sets
    /// how hard it leans on the segment.
    /// </summary>
    public int MaxInFlight { get; init; } = 32;

    /// <summary>Gap before starting the next address, whatever is in flight.</summary>
    public TimeSpan PauseBetween { get; init; } = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// How long to wait after the last echo before reading the ARP cache, so a reply to the last
    /// few ARP requests has time to land in it.
    /// </summary>
    public TimeSpan ArpSettle { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Roughly how long a sweep of <paramref name="addresses"/> takes, for a caption.</summary>
    public TimeSpan Estimate(int addresses)
    {
        double paced = addresses * PauseBetween.TotalSeconds;
        double slots = Math.Ceiling(addresses / (double)Math.Max(1, MaxInFlight)) * Timeout.TotalSeconds * Math.Max(1, Attempts);
        return TimeSpan.FromSeconds(Math.Max(paced, slots)) + ArpSettle;
    }

    internal PingSweepOptions ToPingOptions() => new()
    {
        Timeout = Timeout,
        Attempts = Attempts,
        MaxInFlight = MaxInFlight,
        PauseBetween = PauseBetween,
    };
}
