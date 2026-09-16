namespace NetControl.Core.Tftp;

/// <summary>
/// One block of the file.
///
/// <para>A block shorter than the negotiated block size ends the transfer. That includes a block
/// of length zero, which is not a malformed packet but the legitimate way to end a file whose
/// length is an exact multiple of the block size - a case worth stating because a parser that
/// rejects it turns every exactly-sized file into an unexplained failure.</para>
/// </summary>
/// <param name="Block">The 16-bit block number, first block is 1.</param>
/// <param name="Data">A copy of the payload. May be empty.</param>
public sealed record TftpDataMessage(ushort Block, byte[] Data) : TftpMessage(TftpOpcode.Data)
{
    /// <summary>
    /// Whether this block ends the transfer, given the block size in force. The block size has
    /// to be passed in: a short block only means "last" relative to what was negotiated, and a
    /// receiver that assumes 512 while the sender was granted 1468 truncates the file.
    /// </summary>
    public bool IsFinal(int blockSize) => Data.Length < blockSize;

    public override string Describe() => $"DATA block {Block}, {Data.Length} octets";
}
