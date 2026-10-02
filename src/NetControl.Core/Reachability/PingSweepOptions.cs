namespace NetControl.Core.Reachability;

/// <summary>
/// How hard a sweep leans on the network. The defaults are gentle on purpose - see the scanning
/// rule in CLAUDE.md - and they are options rather than constants so a bench can try others.
/// </summary>
public sealed record PingSweepOptions
{
    /// <summary>How long to wait for each echo reply.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Echo requests per address, stopping at the first reply.
    ///
    /// <para>Two by default rather than one, because the first echo to a device this PC has not
    /// talked to yet is regularly lost while ARP resolves - and a sweep that reports a quarter of a
    /// healthy panel as silent is one nobody believes the second time.</para>
    /// </summary>
    public int Attempts { get; init; } = 2;

    /// <summary>Addresses being pinged at once.</summary>
    public int MaxInFlight { get; init; } = 4;

    /// <summary>Gap before starting the next address, whatever is in flight.</summary>
    public TimeSpan PauseBetween { get; init; } = TimeSpan.FromMilliseconds(50);
}
