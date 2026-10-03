namespace NetControl.Core;

/// <summary>One quirk, in words: what it is called in a list, what it means, and what the tool does about it.</summary>
/// <param name="Flag">The flag.</param>
/// <param name="Label">The checkbox text.</param>
/// <param name="Short">One or two words for the grid cell.</param>
/// <param name="Meaning">What the device does.</param>
/// <param name="Effect">What NetControl does differently because of it - or that it only informs.</param>
public sealed record DeviceQuirkInfo(DeviceQuirks Flag, string Label, string Short, string Meaning, string Effect);
