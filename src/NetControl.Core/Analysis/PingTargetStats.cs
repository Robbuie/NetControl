using System.Globalization;
using System.Net;

namespace NetControl.Core.Analysis;

/// <summary>One target's record in the ping monitor, at one moment. Immutable; safe to bind.</summary>
public sealed record PingTargetStats
{
    public required IPAddress Address { get; init; }

    /// <summary>The name the user or the plan gave it.</summary>
    public string? Label { get; init; }

    public long Sent { get; init; }

    public long Received { get; init; }

    public long Lost => Sent - Received;

    public double LossPercent => Sent == 0 ? 0 : 100.0 * Lost / Sent;

    public double? LastMs { get; init; }

    public double? MinMs { get; init; }

    public double? AverageMs { get; init; }

    public double? MaxMs { get; init; }

    /// <summary>Mean difference between consecutive round trips - a link that is fine on average and bad in bursts shows here.</summary>
    public double? JitterMs { get; init; }

    /// <summary>Echoes lost in a row right now. Zero while it is answering.</summary>
    public int ConsecutiveLost { get; init; }

    /// <summary>Separate outages - runs of at least <see cref="PingMonitor.OutageAfter"/> lost in a row.</summary>
    public int Outages { get; init; }

    /// <summary>The longest outage seen.</summary>
    public TimeSpan LongestOutage { get; init; }

    /// <summary>When the current outage started; null while answering.</summary>
    public DateTimeOffset? DownSinceUtc { get; init; }

    public DateTimeOffset? LastOutageEndUtc { get; init; }

    /// <summary>Round trips for the last samples, oldest first; null for a lost echo.</summary>
    public IReadOnlyList<double?> History { get; init; } = [];

    public bool IsDown => DownSinceUtc is not null;

    public string StateText => Sent == 0
        ? "waiting"
        : IsDown
            ? "DOWN"
            : ConsecutiveLost > 0 ? "dropping" : "up";

    public string LastText => LastMs is { } ms ? Ms(ms) : Sent == 0 ? string.Empty : "lost";

    public string RangeText => MinMs is { } min && AverageMs is { } avg && MaxMs is { } max
        ? $"{Ms(min)} / {Ms(avg)} / {Ms(max)}"
        : string.Empty;

    public string JitterText => JitterMs is { } j ? Ms(j) : string.Empty;

    public string LossText => string.Create(CultureInfo.InvariantCulture, $"{LossPercent:0.#}% ({Lost}/{Sent})");

    public string OutageText => Outages == 0
        ? "none"
        : string.Create(CultureInfo.InvariantCulture, $"{Outages}, longest {LongestOutage.TotalSeconds:0.#} s");

    private static string Ms(double ms) => ms < 1
        ? "<1 ms"
        : string.Create(CultureInfo.InvariantCulture, $"{ms:0} ms");
}
