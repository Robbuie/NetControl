namespace NetControl.Core.Tftp;

/// <summary>
/// A read or write request: the first packet of every transfer, and the only one that says what
/// is being asked for.
///
/// <para>This is the packet the whole watch mode exists to capture. A controller asking for a
/// path the server does not have, or asking with a drive prefix or a leading separator the
/// server rejects, is a five-second fix - and no TFTP server logs the name it refused. So
/// <see cref="FileName"/> is kept exactly as sent: not trimmed, not normalised, not
/// case-folded, separators untouched. <see cref="FileNameBytes"/> is beside it for the case
/// where the name is not printable at all and the bytes are the only honest answer.</para>
/// </summary>
/// <param name="Opcode"><see cref="TftpOpcode.ReadRequest"/> or <see cref="TftpOpcode.WriteRequest"/>.</param>
/// <param name="FileName">The name as sent, decoded byte-for-byte. Never altered.</param>
/// <param name="FileNameBytes">The same name un-decoded, for a name that will not print.</param>
/// <param name="Mode">The transfer mode, recognised.</param>
/// <param name="RawMode">The mode string as sent, whatever it was.</param>
/// <param name="Options">The RFC 2347 options offered, in the order they were offered.</param>
public sealed record TftpRequestMessage(
    TftpOpcode Opcode,
    string FileName,
    byte[] FileNameBytes,
    TftpTransferMode Mode,
    string RawMode,
    TftpOptions Options) : TftpMessage(Opcode)
{
    /// <summary>True for a write request - a device pushing a file at the server, such as a backup.</summary>
    public bool IsWrite => Opcode == TftpOpcode.WriteRequest;

    /// <summary>
    /// True when this request would corrupt a binary file in flight. An image backup asked for
    /// in netascii arrives the right length and wrong in every line ending it contains, and
    /// nothing in the transfer reports a problem.
    /// </summary>
    public bool ModeWouldCorruptBinary => Mode == TftpTransferMode.NetAscii;

    public override string Describe()
    {
        string verb = IsWrite ? "WRQ" : "RRQ";
        string options = Options.IsEmpty ? string.Empty : $", options {Options}";

        return $"{verb} '{FileName}' mode {RawMode}{options}";
    }

    /// <summary>
    /// What is worth saying about this request beyond the fact that it arrived, or an empty list
    /// when there is nothing. Each entry names a cause and is safe to show as-is.
    /// </summary>
    public IReadOnlyList<string> Concerns()
    {
        var concerns = new List<string>();

        if (ModeWouldCorruptBinary)
        {
            concerns.Add(
                $"The request asks for '{RawMode}' mode, which translates line endings. A robot image "
                + "transferred this way arrives corrupted and nothing reports an error - it should be "
                + "'octet'.");
        }
        else if (Mode == TftpTransferMode.Unknown)
        {
            concerns.Add(
                $"The transfer mode '{RawMode}' is not one this tool recognises. Most servers will "
                + "refuse it outright with an illegal-operation error.");
        }

        if (FileName.Length == 0)
        {
            concerns.Add("The request carries an empty filename, which every server will refuse.");
        }

        if (Options.Has(TftpOption.BlockSizeName)
            && Options.BlockSize is { } size
            && !TftpLimits.IsBlockSizeInRange(size))
        {
            concerns.Add(
                $"blksize was asked for as {size}, outside RFC 2348's {TftpLimits.MinBlockSize}-"
                + $"{TftpLimits.MaxBlockSize}. A server that follows the RFC will ignore the option and "
                + "fall back to 512-octet blocks.");
        }

        // A transfer that has TOLD US its size and cannot carry it without wrapping the block
        // counter is a real finding, and the size came from the controller rather than from a
        // guess. "No blksize was offered" on its own is not a finding: RFC 1350 defaults are the
        // ordinary case, the options are reported on every row anyway, and a concern raised
        // against every plain request is one people learn to skip - which is the same reasoning
        // that keeps PlanConformance silent about a device that did not answer.
        if (Options.TransferSize is { } declared
            && TftpLimits.DescribeRolloverRisk(declared, Options.EffectiveBlockSize) is { } risk)
        {
            concerns.Add(risk);
        }

        return concerns;
    }
}
