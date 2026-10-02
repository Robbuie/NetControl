namespace NetControl.Core.Cip;

/// <summary>
/// Ethernet Link object (0xF6) attribute 2, Interface Flags, decoded.
///
/// <para>Not a <c>[Flags]</c> enum because bits 2-4 are a three-bit number rather than three flags,
/// and an enum would let somebody test one of them on its own and get a meaningless answer.</para>
/// </summary>
public readonly record struct LinkFlags(uint Raw)
{
    /// <summary>Bit 0: the port has link.</summary>
    public bool LinkActive => (Raw & 0x01) != 0;

    /// <summary>Bit 1: full duplex. Only meaningful while <see cref="LinkActive"/>.</summary>
    public bool FullDuplex => (Raw & 0x02) != 0;

    /// <summary>Bits 2-4.</summary>
    public NegotiationStatus Negotiation => ((Raw >> 2) & 0x07) switch
    {
        0 => NegotiationStatus.InProgress,
        1 => NegotiationStatus.FailedUsingDefaults,
        2 => NegotiationStatus.DuplexDefaulted,
        3 => NegotiationStatus.Negotiated,
        4 => NegotiationStatus.Forced,
        _ => NegotiationStatus.Reserved,
    };

    /// <summary>Bit 5: a speed/duplex change has been written and is waiting for a reset.</summary>
    public bool ManualSettingRequiresReset => (Raw & 0x20) != 0;

    /// <summary>Bit 6: the interface reports a fault in its own hardware.</summary>
    public bool LocalHardwareFault => (Raw & 0x40) != 0;

    public override string ToString() =>
        !LinkActive
            ? "no link"
            : $"{(FullDuplex ? "full" : "half")} duplex, {Negotiation}"
                + (LocalHardwareFault ? ", hardware fault" : string.Empty)
                + (ManualSettingRequiresReset ? ", reset pending" : string.Empty);
}
