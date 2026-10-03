namespace NetControl.Core.Reachability;

/// <summary>
/// Which handshake to try on which well-known port. A port that is not in here is reported open or
/// closed and nothing more - which is the right answer for HTTPS (a TLS handshake proves TLS, not
/// the device), DNP3 and GE SRTP, where the smallest honest question is not small.
/// </summary>
public static class ProtocolHandshakes
{
    /// <summary>The default table, keyed by TCP port.</summary>
    public static IReadOnlyDictionary<int, IProtocolHandshake> ByPort { get; } =
        new Dictionary<int, IProtocolHandshake>
        {
            [21] = new BannerHandshake(BannerKind.Ftp),
            [22] = new BannerHandshake(BannerKind.Ssh),
            [23] = new BannerHandshake(BannerKind.Telnet),
            [80] = new HttpHandshake(),
            [102] = new IsoTsapHandshake(),
            [502] = new ModbusHandshake(),
            [4840] = new OpcUaHandshake(),
            [5007] = new MelsecHandshake(),
            [44818] = new EnipHandshake(),
        };
}
