namespace NetControl.Core.Tftp;

/// <summary>
/// TFTP error codes, and what they mean to somebody standing next to a robot that will not back
/// up.
///
/// <para>The wording is the product here, exactly as it is in <c>CipGeneralStatus</c>. RFC 1350
/// calls code 2 "Access violation", which sends a controls engineer nowhere; the two things that
/// actually cause it are a root folder the server account cannot write to and a file that
/// already exists on a server with overwrite turned off, and naming both sends them to the
/// server.</para>
///
/// <para>The server's own message string is always carried alongside, never replaced. Most
/// servers reach for code 0 rather than choosing a code, so that string is frequently the only
/// specific thing in the packet.</para>
/// </summary>
public static class TftpErrorText
{
    /// <summary>
    /// One sentence naming the likely cause, and one naming the next action.
    /// </summary>
    /// <param name="code">The code from the error packet.</param>
    /// <param name="serverMessage">The server's own string. May be empty.</param>
    public static (string Summary, string? Remediation) Describe(TftpErrorCode code, string? serverMessage)
    {
        string quoted = string.IsNullOrWhiteSpace(serverMessage)
            ? string.Empty
            : $" The server said: \"{serverMessage.Trim()}\".";

        (string summary, string? remediation) = code switch
        {
            TftpErrorCode.NotDefined => (
                "The server refused the transfer without saying which kind of refusal it was - code 0 is "
                + "what most servers send instead of choosing a code.",
                "Read the server's own message above; it is the only specific thing in this packet. If it "
                + "is empty, check the server's log for the same moment."),

            TftpErrorCode.FileNotFound => (
                "The server could not find the path. On a write this almost always means the directory "
                + "does not exist rather than the file - TFTP cannot create directories, so every folder "
                + "in the path has to be there already.",
                "Check the server's root folder against the exact path the request asked for, and create "
                + "any missing subfolder by hand."),

            TftpErrorCode.AccessViolation => (
                "The server refused to write. The two usual causes are a root folder the server's account "
                + "cannot write to, and a file that already exists on a server with overwrite turned off.",
                "Check that the account the TFTP service runs as can write to the root folder, and that "
                + "the server is configured to allow overwriting an existing file."),

            TftpErrorCode.DiskFull => (
                "The server ran out of room, or hit a per-file size limit. A robot image is large enough "
                + "for both to be real.",
                "Check free space on the volume holding the TFTP root, and any maximum-file-size setting "
                + "the server offers."),

            TftpErrorCode.IllegalOperation => (
                "The server rejected the request as malformed. This is what a transfer mode or an option "
                + "the server does not implement usually comes back as.",
                "Check the transfer mode is 'octet' and try again offering no options at all - if it "
                + "succeeds without them, the server does not implement RFC 2347 negotiation."),

            TftpErrorCode.UnknownTransferId => (
                "A datagram arrived from a port neither end was expecting. This is the signature of a "
                + "firewall or NAT rewriting the ephemeral data port: TFTP moves off port 69 for the "
                + "transfer itself, and anything that does not track that breaks it here.",
                "Check for a stateful firewall or NAT between the two, and whether it has a TFTP helper "
                + "enabled. A multi-homed server answering from a second address does this too."),

            TftpErrorCode.FileAlreadyExists => (
                "The file is already there and the server will not overwrite it.",
                "Turn on overwrite in the server, or move the previous backup out of the way before "
                + "starting this one."),

            TftpErrorCode.NoSuchUser => (
                "The server rejected the transfer for the 'mail' mode's user, which nothing current "
                + "implements. Seeing this at all suggests the mode field was misread.",
                "Check the transfer mode in the request; it should be 'octet'."),

            TftpErrorCode.OptionNegotiationFailed => (
                "The server refused the RFC 2347 options and ended the transfer rather than continuing "
                + "without them.",
                "Retry offering no options. A server that refuses negotiation outright cannot do blksize "
                + "either, which caps any transfer at about 32 MB."),

            _ => (
                $"The server sent error code {(ushort)code}, which is not one RFC 1350 or RFC 2347 "
                + "defines.",
                "Read the server's own message, and check the server's documentation for that code."),
        };

        return (summary + quoted, remediation);
    }
}
