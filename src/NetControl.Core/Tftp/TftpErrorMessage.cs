namespace NetControl.Core.Tftp;

/// <summary>
/// An error, which in TFTP also ends the transfer: there is no recovery and no reply to it.
/// </summary>
/// <param name="Code">The RFC 1350 code.</param>
/// <param name="Message">The far end's own words, kept verbatim. Frequently the only useful part.</param>
public sealed record TftpErrorMessage(TftpErrorCode Code, string Message) : TftpMessage(TftpOpcode.Error)
{
    public override string Describe()
    {
        string text = Message.Length == 0 ? "(no message)" : Message;

        return $"ERROR {(ushort)Code} ({Code}): {text}";
    }

    /// <summary>The wording a user should see: our sentence, and what to do next.</summary>
    public (string Summary, string? Remediation) Explain() => TftpErrorText.Describe(Code, Message);
}
