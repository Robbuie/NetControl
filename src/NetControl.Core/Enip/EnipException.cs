namespace NetControl.Core.Enip;

/// <summary>
/// Something went wrong talking EtherNet/IP to a device: the encapsulation layer refused, the
/// framing did not decode, or the connection went away mid-message.
///
/// <para>Deliberately not used for a CIP request the device answered with a non-zero status. That
/// is not an error in the conversation - the device understood perfectly and said no - and it
/// carries its own status code and its own explanation. See <see cref="Cip.CipResponse"/>.</para>
/// </summary>
public sealed class EnipException : NetControlException
{
    public EnipException(string message)
        : base(message)
    {
    }

    public EnipException(string message, Exception? inner)
        : base(message, inner)
    {
    }
}
