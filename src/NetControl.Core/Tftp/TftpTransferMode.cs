namespace NetControl.Core.Tftp;

/// <summary>
/// The transfer mode named in a read or write request.
///
/// <para>This is a first-class field rather than a detail because of what the wrong one does to
/// a robot image. <see cref="NetAscii"/> translates line endings in flight; applied to a
/// firmware or FROM image it corrupts the file and reports success, which is the single worst
/// failure mode available in this protocol. The tool grades the mode on every request rather
/// than recording it in passing.</para>
/// </summary>
public enum TftpTransferMode
{
    /// <summary>
    /// The request named something this tool does not recognise. The string as sent is kept on
    /// <see cref="TftpRequestMessage.RawMode"/>; it is never guessed at or normalised away.
    /// </summary>
    Unknown = 0,

    /// <summary>Line endings are translated. Correct for text, ruinous for an image.</summary>
    NetAscii = 1,

    /// <summary>Byte for byte. What any binary transfer must use.</summary>
    Octet = 2,

    /// <summary>Obsolete since RFC 1350 and not implemented by anything current.</summary>
    Mail = 3,
}
