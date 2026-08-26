namespace NetControl.Core.Interfaces;

/// <summary>
/// Reads the machine's ARP cache.
///
/// <para>An interface because the real one is a P/Invoke into a table nobody can arrange the
/// contents of, and the interesting cases - a device that answered a scan but is not in the cache,
/// an entry learned on the wrong adapter - are exactly the ones a test has to be able to state
/// outright.</para>
/// </summary>
public interface IArpLookup
{
    /// <summary>
    /// Whether the cache can be read at all on this machine. False is not "the table is empty":
    /// it is "we did not look", and a caller must word its answer differently for the two, the
    /// same way <c>PortConflictDetector</c> does.
    /// </summary>
    bool IsSupported { get; }

    /// <summary>
    /// Every usable IPv4 entry, newest read each time. Deliberately not cached: it is called
    /// immediately after a scan precisely because the replies have just refreshed it, and a cached
    /// answer would be the state of the world before the question was asked.
    /// </summary>
    IReadOnlyList<ArpEntry> Snapshot();
}
