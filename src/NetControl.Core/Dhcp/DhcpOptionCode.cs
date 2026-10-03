namespace NetControl.Core.Dhcp;

/// <summary>
/// The RFC 2132 option codes this tool actually uses. Named constants rather than magic numbers,
/// because "54" in a packet builder is unreadable and "SubnetMask" versus "ServerIdentifier" is
/// exactly the kind of thing that gets transposed at 2am on a commissioning job.
/// </summary>
public static class DhcpOptionCode
{
    public const byte Pad = 0;
    public const byte SubnetMask = 1;
    public const byte Router = 3;
    public const byte DomainNameServer = 6;
    public const byte HostName = 12;
    public const byte DomainName = 15;
    public const byte RequestedIpAddress = 50;
    public const byte IpAddressLeaseTime = 51;

    /// <summary>Option overload: the file and/or sname fields carry options instead of strings.</summary>
    public const byte OptionOverload = 52;

    public const byte DhcpMessageType = 53;
    public const byte ServerIdentifier = 54;
    public const byte ParameterRequestList = 55;
    public const byte Message = 56;
    public const byte VendorClassIdentifier = 60;
    public const byte ClientIdentifier = 61;

    /// <summary>TFTP server name (RFC 2132 9.4). The DHCP-option twin of the header's sname field.</summary>
    public const byte TftpServerName = 66;

    /// <summary>Bootfile name (RFC 2132 9.5). The DHCP-option twin of the header's file field.</summary>
    public const byte BootFileName = 67;
    public const byte End = 255;
}
