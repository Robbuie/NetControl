using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.Versioning;

namespace NetControl.Core.Interfaces;

/// <summary>
/// This PC's own adapter counters, at one moment - the laptop's half of a link.
///
/// <para>Here because a damaged patch lead between the laptop and the switch looks, from everywhere
/// else in the tool, exactly like a device problem: requests that never arrive, replies that are not
/// taken, scans that find half a panel. Two reads of these say whether it is the laptop's own cable
/// before anybody opens a cabinet.</para>
/// </summary>
public sealed record NicCounters
{
    public required int Index { get; init; }

    public required string Name { get; init; }

    public required DateTimeOffset ReadUtc { get; init; }

    public long ReceivedWithErrors { get; init; }

    public long ReceivedDiscarded { get; init; }

    public long SentWithErrors { get; init; }

    public long SentDiscarded { get; init; }

    public long UnicastReceived { get; init; }

    public long NonUnicastReceived { get; init; }

    /// <summary>
    /// Reads the adapter with this interface index, or null when it is not there any more. Windows
    /// only: several of these counters are not reported on other platforms, and this tool is Windows
    /// only anyway.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static NicCounters? Read(int interfaceIndex, TimeProvider? time = null)
    {
        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            IPv4InterfaceProperties? v4;

            try
            {
                v4 = nic.GetIPProperties().GetIPv4Properties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            if (v4 is null || v4.Index != interfaceIndex)
            {
                continue;
            }

            IPInterfaceStatistics stats = nic.GetIPStatistics();

            return new NicCounters
            {
                Index = interfaceIndex,
                Name = nic.Name,
                ReadUtc = (time ?? TimeProvider.System).GetUtcNow(),
                ReceivedWithErrors = stats.IncomingPacketsWithErrors,
                ReceivedDiscarded = stats.IncomingPacketsDiscarded,
                SentWithErrors = stats.OutgoingPacketsWithErrors,
                SentDiscarded = stats.OutgoingPacketsDiscarded,
                UnicastReceived = stats.UnicastPacketsReceived,
                NonUnicastReceived = stats.NonUnicastPacketsReceived,
            };
        }

        return null;
    }

    /// <summary>
    /// What these counters say, compared with an earlier read of the same adapter when there is one.
    /// As with the device counters, a total is a total and only a counter that moved between two reads
    /// is called a problem.
    /// </summary>
    public string Describe(NicCounters? earlier = null)
    {
        string totals = string.Create(
            CultureInfo.InvariantCulture,
            $"{Name}: received {UnicastReceived:N0} unicast and {NonUnicastReceived:N0} broadcast/multicast; "
            + $"{ReceivedWithErrors:N0} received with errors, {ReceivedDiscarded:N0} discarded, "
            + $"{SentWithErrors:N0} sent with errors.");

        if (earlier is null || earlier.Index != Index || ReadUtc <= earlier.ReadUtc)
        {
            return totals + " These are totals since the adapter came up - read again in a minute to see whether "
                + "errors are still being counted.";
        }

        long errors = (ReceivedWithErrors - earlier.ReceivedWithErrors) + (SentWithErrors - earlier.SentWithErrors);
        long discards = ReceivedDiscarded - earlier.ReceivedDiscarded;
        double seconds = (ReadUtc - earlier.ReadUtc).TotalSeconds;

        if (errors < 0 || discards < 0)
        {
            return totals + " The counters went backwards since the last read - the adapter was reset.";
        }

        if (errors == 0 && discards == 0)
        {
            return totals + string.Create(
                CultureInfo.InvariantCulture,
                $" Nothing new in the last {seconds:0} s - this PC's own link is clean.");
        }

        return totals + string.Create(
            CultureInfo.InvariantCulture,
            $" {errors:N0} error(s) and {discards:N0} discard(s) in the last {seconds:0} s. Errors on this PC's "
            + $"own adapter are this PC's cable, port or dock - swap the patch lead before suspecting the devices.");
    }

    /// <summary>True when the read since <paramref name="earlier"/> counted new errors.</summary>
    public bool HasNewErrorsSince(NicCounters earlier)
    {
        ArgumentNullException.ThrowIfNull(earlier);

        return earlier.Index == Index
            && (ReceivedWithErrors > earlier.ReceivedWithErrors || SentWithErrors > earlier.SentWithErrors);
    }
}
