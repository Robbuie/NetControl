using System.Globalization;
using System.Text;
using NetControl.Core.Persistence;

namespace NetControl.Core.Reachability;

/// <summary>
/// Every address and what it said, in the order they were asked about.
/// </summary>
public sealed record PingSweepResult(IReadOnlyList<PingOutcome> Outcomes, TimeSpan Duration)
{
    public int Replied => Outcomes.Count(o => o.Status == PingStatus.Replied);

    public int Silent => Outcomes.Count(o => o.Status == PingStatus.TimedOut);

    public int Unreachable => Outcomes.Count(o => o.Status == PingStatus.Unreachable);

    public int Failed => Outcomes.Count(o => o.Status == PingStatus.Failed);

    /// <summary>
    /// Warn when nothing at all answered, or when this PC had no route to something. Both say more
    /// about the laptop than about the devices, and both are worth finding in the record later.
    /// Silence from some devices is Info: many drives and I/O adapters are set to ignore ping.
    /// </summary>
    public EventSeverity Severity =>
        Outcomes.Count > 0 && (Replied == 0 || Unreachable > 0 || Failed > 0)
            ? EventSeverity.Warn
            : EventSeverity.Info;

    /// <summary>The event row's message, and the status line under the button. One sentence.</summary>
    public string Summary
    {
        get
        {
            if (Outcomes.Count == 1)
            {
                return Outcomes[0].Sentence;
            }

            var text = new StringBuilder();
            text.Append(CultureInfo.InvariantCulture, $"Pinged {Outcomes.Count} address(es): {Replied} answered");

            if (Silent > 0)
            {
                text.Append(CultureInfo.InvariantCulture, $", {Silent} did not");
            }

            if (Unreachable > 0)
            {
                text.Append(CultureInfo.InvariantCulture, $", {Unreachable} had no route from this PC");
            }

            if (Failed > 0)
            {
                text.Append(CultureInfo.InvariantCulture, $", {Failed} could not be pinged");
            }

            text.Append('.');

            if (Outcomes.Count > 0 && Replied == 0)
            {
                text.Append(" Nothing answered at all - check the adapter and its subnet before suspecting every "
                    + "device at once.");
            }

            return text.ToString();
        }
    }
}
