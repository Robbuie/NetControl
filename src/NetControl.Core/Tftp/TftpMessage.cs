namespace NetControl.Core.Tftp;

/// <summary>
/// Any one TFTP datagram, decoded.
///
/// <para>A closed hierarchy rather than one mutable class with nullable fields, because the six
/// packet types share almost nothing but their first two bytes, and because the watch server has
/// to be able to receive whatever turns up and say what it was. A parser that returned a
/// half-populated object would push that decision to every caller.</para>
/// </summary>
public abstract record TftpMessage(TftpOpcode Opcode)
{
    /// <summary>
    /// One line for the log, already user-facing. Overridden by every message type, because
    /// "Data" tells nobody anything and "DATA block 1, 512 octets" tells them where a stalled
    /// transfer stopped.
    /// </summary>
    public abstract string Describe();
}
