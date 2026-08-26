namespace NetControl.Core.Commissioning;

/// <summary>
/// One step of a commissioning run, as it happens.
///
/// <para>Raised on whatever thread the run is on, so a UI marshals - same rule as the DHCP
/// server's events. These exist so the operation is visible while it is happening: it takes
/// seconds, it touches live equipment, and a progress bar that only says "working" is not an
/// acceptable account of a write to a plant device.</para>
/// </summary>
public sealed class CommissionProgressEventArgs(CommissionStep step, string message, bool isFailure = false)
    : EventArgs
{
    public CommissionStep Step { get; } = step;

    /// <summary>Already user-facing. Core writes these; the UI does not rephrase them.</summary>
    public string Message { get; } = message;

    /// <summary>True when this step is where it went wrong.</summary>
    public bool IsFailure { get; } = isFailure;
}
