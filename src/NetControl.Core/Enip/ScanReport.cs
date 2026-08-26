using System.Globalization;
using NetControl.Core.Interfaces;

namespace NetControl.Core.Enip;

/// <summary>
/// What one scan did and what came back.
///
/// <para>The counts are not decoration. A scan that put packets on a plant network has to be able
/// to account for itself in the event log, and "found nothing" and "found nothing, and eleven
/// hosts actively refused the probe" are different facts with different next actions.</para>
/// </summary>
/// <param name="Nic">The adapter the probes left by.</param>
/// <param name="Replies">One entry per distinct device that answered, in the order they answered.</param>
/// <param name="ProbesSent">Datagrams transmitted, the broadcast included.</param>
/// <param name="Duplicates">Replies collapsed because that device had already answered this scan.</param>
/// <param name="Ignored">Datagrams that arrived on the socket and were not a ListIdentity reply.</param>
/// <param name="Refused">
/// Probes answered with an ICMP port-unreachable: something is at that address, but it is not
/// speaking EtherNet/IP. Useful, and the reason a unicast sweep is worth more than it looks.
/// </param>
/// <param name="Duration">Wall clock, probes and listening window together.</param>
public sealed record ScanReport(
    NicInfo Nic,
    IReadOnlyList<ListIdentityReply> Replies,
    int ProbesSent,
    int Duplicates,
    int Ignored,
    int Refused,
    TimeSpan Duration)
{
    /// <summary>One line for the event log. Written here so the log and the UI cannot disagree.</summary>
    public string Summary
    {
        get
        {
            string seconds = Duration.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture);

            return $"Scanned from [{Nic.Index}] {Nic.Name}: {ProbesSent} probe(s) in {seconds}s, "
                + $"{Replies.Count} device(s) answered"
                + (Refused > 0 ? $", {Refused} host(s) refused" : string.Empty)
                + (Ignored > 0 ? $", {Ignored} datagram(s) ignored" : string.Empty)
                + ".";
        }
    }
}
