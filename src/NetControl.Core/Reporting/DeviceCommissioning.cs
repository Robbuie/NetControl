namespace NetControl.Core.Reporting;

/// <summary>One device's last finished commissioning operation, as the record has it.</summary>
/// <param name="DeviceId">The plan row.</param>
/// <param name="Outcome">How it ended.</param>
/// <param name="Utc">When.</param>
/// <param name="Message">The record's own sentence for it - Core's words, unchanged.</param>
/// <param name="Readback">What the device reported when read back, when it was.</param>
public sealed record DeviceCommissioning(
    long DeviceId, CommissioningOutcome Outcome, DateTimeOffset Utc, string Message, string? Readback);
