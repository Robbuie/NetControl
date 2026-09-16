namespace NetControl.Core.Tftp;

/// <summary>
/// How a TFTP server's root folder stands, when the tool is running on the server and can look
/// at it directly.
///
/// <para>This is the dullest third of the failure space and the most common: a path that does
/// not exist, an account that cannot write to it, a volume with no room on it. None of the three
/// is visible from the robot end, all three present as a backup that will not start, and all
/// three are answerable in a second by whoever is standing at the server.</para>
/// </summary>
public enum TftpRootVerdict
{
    /// <summary>Nothing was checked - no folder configured, or the check could not run. Not good news.</summary>
    Unknown = 0,

    /// <summary>The folder exists, can be written to, and has room.</summary>
    Writable = 1,

    /// <summary>Writable, but with little enough room left that a large image may not fit.</summary>
    LowSpace = 2,

    /// <summary>The folder is there and the write probe was refused.</summary>
    NotWritable = 3,

    /// <summary>There is nothing at that path. TFTP cannot create it - it cannot create directories at all.</summary>
    Missing = 4,
}
