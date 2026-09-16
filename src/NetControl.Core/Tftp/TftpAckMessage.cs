namespace NetControl.Core.Tftp;

/// <summary>
/// An acknowledgement of one block.
///
/// <para>Block 0 is the acknowledgement of a write request - the packet that says the server has
/// accepted the write and the transfer may begin. When a client offered options, an ACK 0 in
/// place of an OACK is how a server says it wants none of them, silently and legally.</para>
/// </summary>
public sealed record TftpAckMessage(ushort Block) : TftpMessage(TftpOpcode.Acknowledgement)
{
    /// <summary>True for the acknowledgement that opens a write transfer.</summary>
    public bool IsWriteAccepted => Block == 0;

    public override string Describe() => $"ACK block {Block}";
}
