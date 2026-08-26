namespace NetControl.Core.Interfaces;

/// <summary>
/// MIB_IPNETROW's dwType. The numbers are Windows', not ours, so they are stated rather than
/// renumbered into something tidier.
/// </summary>
public enum ArpEntryType
{
    /// <summary>Not one of the below - and not a value anything should act on.</summary>
    Other = 1,

    /// <summary>Windows kept the row but knows it is no longer true.</summary>
    Invalid = 2,

    /// <summary>Learned from traffic. This is what a ListIdentity reply leaves behind.</summary>
    Dynamic = 3,

    /// <summary>Configured by hand, usually with <c>arp -s</c>.</summary>
    Static = 4,
}
