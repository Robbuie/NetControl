using System.Net;

namespace NetControl.Core.Analysis;

/// <summary>One source on the traffic monitor's top-talkers list.</summary>
/// <param name="Mac">The source MAC.</param>
/// <param name="Address">The last IPv4 address it sent from, when it sent any.</param>
/// <param name="PerSecond">Its recent rate, averaged over about ten seconds.</param>
/// <param name="Total">Frames from it since listening started.</param>
/// <param name="Vendor">From the OUI table, when the monitor was given one.</param>
public sealed record TrafficTalker(MacAddress Mac, IPAddress? Address, double PerSecond, long Total, string? Vendor);
