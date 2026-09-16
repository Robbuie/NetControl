namespace NetControl.Core.Tftp;

/// <summary>
/// RFC 2347 option acknowledgement: the options the far end is willing to use, which is a subset
/// of what was asked for and never a superset.
///
/// <para>Receiving one of these is the only positive evidence that option negotiation worked.
/// Its absence is not an error and not a refusal to transfer - it is a transfer about to run on
/// RFC 1350 defaults, which is a different thing and needs saying differently.</para>
/// </summary>
public sealed record TftpOptionAckMessage(TftpOptions Options) : TftpMessage(TftpOpcode.OptionAcknowledgement)
{
    public override string Describe() => $"OACK {Options}";
}
