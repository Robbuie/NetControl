using System.Net;
using NetControl.Core.Persistence;

namespace NetControl.Core.Discovery;

/// <summary>One change between two scans, worded for somebody standing at the panel.</summary>
/// <param name="Kind">What sort of change.</param>
/// <param name="Severity">Warn for a change that somebody did to the network; Info for one that is worth knowing.</param>
/// <param name="Address">The address it is about now, or for a device that went silent, where it was.</param>
/// <param name="Message">Already user-facing.</param>
public sealed record InventoryChange(
    InventoryChangeKind Kind, EventSeverity Severity, IPAddress Address, string Message)
{
    public override string ToString() => Message;
}
