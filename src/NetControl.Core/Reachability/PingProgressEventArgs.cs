namespace NetControl.Core.Reachability;

/// <summary>
/// One address answered, or did not. Raised on whatever thread the ping completed on, so a UI
/// marshals - the same rule as every other Core event.
/// </summary>
public sealed class PingProgressEventArgs(PingOutcome outcome, int completed, int total) : EventArgs
{
    public PingOutcome Outcome { get; } = outcome;

    /// <summary>How many addresses have an answer so far, this one included.</summary>
    public int Completed { get; } = completed;

    public int Total { get; } = total;
}
