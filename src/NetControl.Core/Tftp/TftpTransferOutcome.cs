namespace NetControl.Core.Tftp;

/// <summary>
/// How a transfer ended. A failure always carries a sentence that names the cause and, where there
/// is one, what to do - the same rule as every other error in this tool.
/// </summary>
/// <param name="Succeeded">The last block went across and was acknowledged.</param>
/// <param name="Stats">What moved before it ended, whether or not it succeeded.</param>
/// <param name="Failure">Why it stopped, or null when it succeeded.</param>
/// <param name="Remediation">What to do about it, when there is something.</param>
public sealed record TftpTransferOutcome(
    bool Succeeded,
    TftpTransferStats Stats,
    string? Failure = null,
    string? Remediation = null)
{
    /// <summary>The ERROR the other end sent, when that is what stopped it.</summary>
    public TftpErrorMessage? PeerError { get; init; }

    /// <summary>
    /// The block the transfer was waiting on when it gave up, counted without wrapping - so a
    /// stall at 65,536 is a stall at the block counter's rollover, and says so.
    /// </summary>
    public long? StalledAtBlock { get; init; }

    /// <summary>Windows reported the other end's port closed (an ICMP port unreachable came back).</summary>
    public bool PeerUnreachable { get; init; }

    /// <summary>True when the transfer stopped exactly where the 16-bit block counter rolls over.</summary>
    public bool StalledAtRollover => StalledAtBlock is { } block && block % (TftpLimits.LastBlockNumber + 1L) == 0;
}
