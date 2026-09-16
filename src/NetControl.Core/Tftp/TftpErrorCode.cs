namespace NetControl.Core.Tftp;

/// <summary>
/// RFC 1350 error codes, plus RFC 2347's code 8.
///
/// <para>The numbers are on the wire; the words a user sees come from
/// <see cref="TftpErrorText"/> and are deliberately not these names.</para>
/// </summary>
public enum TftpErrorCode : ushort
{
    /// <summary>
    /// "Not defined, see error message". The most common code in practice, because most servers
    /// reach for it rather than choosing, which is why the server's own message string has to be
    /// carried alongside it everywhere.
    /// </summary>
    NotDefined = 0,

    FileNotFound = 1,

    /// <summary>The one a refused image backup usually comes back as.</summary>
    AccessViolation = 2,

    DiskFull = 3,

    IllegalOperation = 4,

    /// <summary>
    /// Unknown transfer ID. Sent by whichever end received a datagram from a port it was not
    /// talking to - which is what a firewall rewriting the ephemeral data port looks like from
    /// the inside.
    /// </summary>
    UnknownTransferId = 5,

    FileAlreadyExists = 6,

    NoSuchUser = 7,

    /// <summary>RFC 2347: the option negotiation itself was refused, and the transfer is over.</summary>
    OptionNegotiationFailed = 8,
}
