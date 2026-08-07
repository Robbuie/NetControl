namespace NetControl.Core.Dhcp;

/// <summary>Anything the BOOTP/DHCP engine refuses to do, or cannot.</summary>
public class DhcpException : NetControlException
{
    public DhcpException(string message) : base(message) { }

    public DhcpException(string message, Exception? inner) : base(message, inner) { }
}
