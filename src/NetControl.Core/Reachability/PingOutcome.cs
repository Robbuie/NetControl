using System.Globalization;
using System.Net;

namespace NetControl.Core.Reachability;

/// <summary>What one address said, after however many attempts it took.</summary>
public sealed record PingOutcome
{
    public required IPAddress Address { get; init; }

    public required PingStatus Status { get; init; }

    /// <summary>Round trip of the reply that counted. Null unless <see cref="Status"/> is Replied.</summary>
    public TimeSpan? RoundTrip { get; init; }

    /// <summary>The reply's TTL, when the platform reports it. A low one means routers in between.</summary>
    public int? Ttl { get; init; }

    /// <summary>How many echo requests were sent to get this answer.</summary>
    public int Attempts { get; init; } = 1;

    /// <summary>The platform's own word for a failure, kept for the record. Not shown on its own.</summary>
    public string? Detail { get; init; }

    public bool Replied => Status == PingStatus.Replied;

    /// <summary>Short enough for a grid cell.</summary>
    public string Text => Status switch
    {
        PingStatus.Replied when RoundTrip is { } rtt => rtt < TimeSpan.FromMilliseconds(1)
            ? "<1 ms"
            : string.Create(CultureInfo.InvariantCulture, $"{rtt.TotalMilliseconds:0} ms"),
        PingStatus.Replied => "replied",
        PingStatus.TimedOut => "no reply",
        PingStatus.Unreachable => "no route",
        _ => "failed",
    };

    /// <summary>One sentence, for a tooltip and for the log.</summary>
    public string Sentence => Status switch
    {
        PingStatus.Replied =>
            $"{Address} answered in {Text}"
            + (Attempts > 1 ? $" on attempt {Attempts}" : string.Empty)
            + (Ttl is { } ttl ? string.Create(CultureInfo.InvariantCulture, $" (TTL {ttl}).") : "."),
        PingStatus.TimedOut =>
            $"{Address} did not answer {Attempts} echo request(s). It may be off, unplugged, or set to ignore ping "
            + "- many drives and I/O adapters are.",
        PingStatus.Unreachable =>
            $"There is no route from this PC to {Address}"
            + (Detail is null ? "." : $" ({Detail}).")
            + " Check the adapter is on that subnet.",
        _ => $"Could not ping {Address}" + (Detail is null ? "." : $": {Detail}."),
    };

    public override string ToString() => Sentence;
}
