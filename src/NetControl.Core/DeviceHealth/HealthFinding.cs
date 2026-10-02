using NetControl.Core.Persistence;

namespace NetControl.Core.DeviceHealth;

/// <summary>
/// One thing the diagnostics read found worth saying, worded as a likely cause and a next action.
/// </summary>
/// <param name="Severity">Info for a fact somebody should know, Warn for something to fix, Error for a fault now.</param>
/// <param name="Message">Already user-facing. Shown and recorded as it is.</param>
/// <param name="Port">The Ethernet Link instance it is about, or null when it is about the whole device.</param>
public sealed record HealthFinding(EventSeverity Severity, string Message, ushort? Port = null)
{
    public override string ToString() => Message;
}
