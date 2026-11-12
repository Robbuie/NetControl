using System.Net;

namespace NetControl.Core.Analysis;

/// <summary>A plan row, reduced to what the troubleshooter checks against.</summary>
/// <param name="Name">What the plan calls it.</param>
/// <param name="Address">The planned address, when there is one.</param>
/// <param name="Mask">The planned mask, when there is one.</param>
/// <param name="Mac">The planned MAC, when there is one.</param>
public sealed record PlannedAddress(string Name, IPAddress? Address, IPAddress? Mask, MacAddress? Mac);
