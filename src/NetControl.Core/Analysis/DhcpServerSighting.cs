using System.Net;

namespace NetControl.Core.Analysis;

/// <summary>A BOOTP/DHCP server heard answering on the segment - one that is not this PC.</summary>
/// <param name="Address">The address the replies came from.</param>
/// <param name="Mac">The MAC they came from.</param>
/// <param name="Replies">How many were heard.</param>
/// <param name="LastUtc">When the last one was heard.</param>
/// <param name="LastOffered">The last address it handed out.</param>
/// <param name="LastClient">The MAC it last answered.</param>
public sealed record DhcpServerSighting(
    IPAddress Address,
    MacAddress Mac,
    long Replies,
    DateTimeOffset LastUtc,
    IPAddress? LastOffered,
    MacAddress? LastClient);
