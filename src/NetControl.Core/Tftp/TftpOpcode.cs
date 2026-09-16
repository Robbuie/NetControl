namespace NetControl.Core.Tftp;

/// <summary>
/// The five RFC 1350 opcodes plus the RFC 2347 option acknowledgement.
///
/// <para>Every one of these is two bytes, big-endian, at the front of the datagram. TFTP is
/// big-endian throughout - like BOOTP and unlike everything in <c>Enip/</c> and <c>Cip/</c>,
/// which now live in the same solution. That is the reason this module never uses
/// <see cref="BitConverter"/>: the byte order is named at every call site.</para>
/// </summary>
public enum TftpOpcode : ushort
{
    /// <summary>Not a value that appears on the wire; the parser never returns it.</summary>
    None = 0,

    /// <summary>Read request. The client wants to pull a file.</summary>
    ReadRequest = 1,

    /// <summary>Write request. A robot backing an image up sends this one.</summary>
    WriteRequest = 2,

    Data = 3,

    Acknowledgement = 4,

    Error = 5,

    /// <summary>
    /// RFC 2347. Sent instead of the first ACK or DATA when the server accepts any of the
    /// options the client asked for. A client that offered options and gets a plain ACK 0 back
    /// has been told, politely, that it gets none of them - which is the gap between requested
    /// and granted that no server log shows.
    /// </summary>
    OptionAcknowledgement = 6,
}
