namespace NetControl.Core.Tftp;

/// <summary>
/// Raised for a TFTP operation that cannot proceed. Carries the remediation
/// <see cref="NetControlException"/> requires, because "transfer failed" helps nobody standing
/// next to a robot.
///
/// <para>Not sealed, and for the same reason <c>DhcpException</c> is not: <see
/// cref="TftpBindException"/> derives from it to carry the port-ownership finding, so the UI can
/// name the process holding UDP/69 rather than showing a socket error code.</para>
/// </summary>
public class TftpException : NetControlException
{
    public TftpException(string message) : base(message) { }

    public TftpException(string message, Exception? inner) : base(message, inner) { }

    /// <summary>
    /// The error the far end sent, when this exception came from one. Null when the failure was
    /// local - a timeout, a socket refusal, a malformed reply.
    /// </summary>
    public TftpErrorCode? ReceivedCode { get; init; }

    /// <summary>The server's own words, kept verbatim beside our translation of them.</summary>
    public string? ServerMessage { get; init; }

    /// <summary>
    /// Builds an exception from an error packet, with the wording from <see cref="TftpErrorText"/>
    /// and the server's own string preserved.
    /// </summary>
    public static TftpException FromError(TftpErrorMessage error, string what)
    {
        ArgumentNullException.ThrowIfNull(error);

        (string summary, string? remediation) = TftpErrorText.Describe(error.Code, error.Message);

        return new TftpException($"{what}: {summary}")
        {
            ReceivedCode = error.Code,
            ServerMessage = error.Message,
            Remediation = remediation,
        };
    }
}
