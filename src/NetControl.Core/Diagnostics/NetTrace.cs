namespace NetControl.Core.Diagnostics;

/// <summary>
/// The diagnostic log the network engines write to.
///
/// <para><b>Why ambient rather than injected.</b> The engines - the DHCP server, the scanner, the CIP
/// session, the TFTP watch, the network guard - are constructed in a dozen places, many of them
/// tests that care about nothing but the bytes. Threading an <see cref="ITraceLog"/> through every
/// constructor would touch all of them to add a line of text each. The application sets
/// <see cref="Log"/> once at startup; everywhere else it is <see cref="NullTraceLog"/>, which writes
/// nothing and costs nothing.</para>
///
/// <para>This is the tool's own diagnostic log, never the commissioning record.</para>
/// </summary>
public static class NetTrace
{
    private static ITraceLog _log = NullTraceLog.Instance;

    /// <summary>Where engine lines go. Never null; setting null restores the no-op log.</summary>
    public static ITraceLog Log
    {
        get => Volatile.Read(ref _log);
        set => Volatile.Write(ref _log, value ?? NullTraceLog.Instance);
    }
}
