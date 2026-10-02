namespace NetControl.Core.Cip;

/// <summary>
/// Ethernet Link object (0xF6) attribute 2, bits 2-4: how the port arrived at its speed and duplex.
/// </summary>
public enum NegotiationStatus
{
    /// <summary>Auto-negotiation has not finished yet. Normal for the first second or so after link.</summary>
    InProgress = 0,

    /// <summary>Auto-negotiation and speed detection both failed; the port is on its default.</summary>
    FailedUsingDefaults = 1,

    /// <summary>
    /// Auto-negotiation failed but the speed was detected. Duplex is on its default - which is half,
    /// and is exactly how a duplex mismatch starts.
    /// </summary>
    DuplexDefaulted = 2,

    /// <summary>Speed and duplex negotiated with the far end. The healthy case.</summary>
    Negotiated = 3,

    /// <summary>Not attempted: speed and duplex are forced. The switch port must be forced to match.</summary>
    Forced = 4,

    /// <summary>A value the spec reserves. Reported as the number.</summary>
    Reserved = 7,
}
