namespace NetControl.Core.Cip;

/// <summary>
/// CIP common service codes (Volume 1, Appendix A).
///
/// Only the ones this tool sends. <see cref="Reset"/> is here because a device that reports
/// <see cref="ConfigCapability.InterfaceResetNeeded"/> will not apply a configuration without it -
/// not because anything reads its way into sending one. Nothing in a read or a scan path may
/// reference it.
/// </summary>
public static class CipService
{
    public const byte GetAttributeAll = 0x01;

    public const byte SetAttributeAll = 0x02;

    /// <summary>Identity object service 5. Never sent as a side effect of anything.</summary>
    public const byte Reset = 0x05;

    public const byte GetAttributeSingle = 0x0E;

    public const byte SetAttributeSingle = 0x10;
}
