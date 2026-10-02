using System.Net;

namespace NetControl.Core.Tftp;

/// <summary>
/// Accept mode finished a transfer - received a backup, or sent a file back - successfully or not.
/// </summary>
public sealed class TftpTransferEventArgs(
    DateTimeOffset timestamp,
    TftpRequestMessage request,
    IPEndPoint source,
    string path,
    TftpTransferOutcome outcome) : EventArgs()
{
    public DateTimeOffset Timestamp { get; } = timestamp;

    public TftpRequestMessage Request { get; } = request;

    public IPEndPoint Source { get; } = source;

    /// <summary>Where the file was written, or read from.</summary>
    public string Path { get; } = path;

    public TftpTransferOutcome Outcome { get; } = outcome;

    public bool IsWrite => Request.IsWrite;

    /// <summary>One line: what moved, and how it went.</summary>
    public string Describe()
    {
        string verb = IsWrite ? "Received" : "Sent";
        return Outcome.Succeeded
            ? $"{verb} '{Request.FileName}' {(IsWrite ? "from" : "to")} {Source}: {Outcome.Stats.Describe()}."
            : $"{(IsWrite ? "Receiving" : "Sending")} '{Request.FileName}' {(IsWrite ? "from" : "to")} {Source} "
                + $"failed: {Outcome.Failure}";
    }
}
