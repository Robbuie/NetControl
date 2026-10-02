using System.Globalization;

namespace NetControl.Core.Tftp;

/// <summary>
/// What one transfer did on the wire: how much, how fast, how often a block had to be sent again,
/// and whether the 16-bit block counter wrapped.
///
/// <para>The wrap is the number worth reading. RFC 1350 says nothing about what comes after block
/// 65535, so two ends that disagree - one wrapping to 0, the other to 1 - write a file of the right
/// length with the wrong contents. A transfer that wrapped and still read back identical has settled
/// that question for this pair of ends for good.</para>
/// </summary>
/// <param name="Bytes">Payload bytes moved, not counting headers.</param>
/// <param name="Blocks">DATA blocks moved, counted without wrapping.</param>
/// <param name="BlockSize">The block size the transfer ran at.</param>
/// <param name="Retransmits">Times a packet had to be sent again because its answer did not come.</param>
/// <param name="Elapsed">Wall time from the first DATA block to the last ACK.</param>
/// <param name="Wraps">Times the block counter went past 65535.</param>
/// <param name="PeerRolledOverToOne">The other end followed block 65535 with block 1 rather than 0.</param>
public sealed record TftpTransferStats(
    long Bytes,
    long Blocks,
    int BlockSize,
    int Retransmits,
    TimeSpan Elapsed,
    int Wraps,
    bool PeerRolledOverToOne)
{
    public double BytesPerSecond => Elapsed.TotalSeconds > 0 ? Bytes / Elapsed.TotalSeconds : 0;

    /// <summary>One line, for a log row and the probe's findings.</summary>
    public string Describe()
    {
        string bytes = Bytes.ToString("N0", CultureInfo.InvariantCulture);
        string seconds = Elapsed.TotalSeconds.ToString("N1", CultureInfo.InvariantCulture);
        string rate = (BytesPerSecond / 1024.0).ToString("N0", CultureInfo.InvariantCulture);
        string block = BlockSize.ToString(CultureInfo.InvariantCulture);
        string retries = Retransmits switch
        {
            0 => "no retransmits",
            1 => "1 retransmit",
            _ => Retransmits.ToString(CultureInfo.InvariantCulture) + " retransmits",
        };
        string wraps = Wraps switch
        {
            0 => string.Empty,
            1 => ", block counter wrapped once",
            _ => $", block counter wrapped {Wraps.ToString(CultureInfo.InvariantCulture)} times",
        };
        string toOne = PeerRolledOverToOne ? " (the other end wraps to 1, not 0)" : string.Empty;

        return $"{bytes} bytes in {seconds} s ({rate} KB/s), {block}-byte blocks, {retries}{wraps}{toOne}";
    }
}
