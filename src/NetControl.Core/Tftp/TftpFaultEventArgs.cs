namespace NetControl.Core.Tftp;

/// <summary>
/// Something on UDP/69 the user needs to know about. The message names the likely cause and the
/// remediation names the next action.
///
/// <para>This carries more than errors. A datagram on this port that is not a TFTP packet at all,
/// and a mid-transfer packet arriving on port 69 when transfers are supposed to have moved to an
/// ephemeral port, are both findings rather than noise - the second in particular is what a
/// firewall or NAT mangling the data port looks like from here.</para>
/// </summary>
public sealed class TftpFaultEventArgs(
    DateTimeOffset timestamp,
    string message,
    string? remediation = null,
    Exception? exception = null,
    bool isFatal = false) : EventArgs()
{
    public DateTimeOffset Timestamp { get; } = timestamp;

    public string Message { get; } = message;

    public string? Remediation { get; } = remediation;

    public Exception? Exception { get; } = exception;

    /// <summary>True when the watch has stopped. The UI must not keep showing a green light.</summary>
    public bool IsFatal { get; } = isFatal;
}
