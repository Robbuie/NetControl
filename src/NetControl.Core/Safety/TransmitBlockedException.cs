namespace NetControl.Core.Safety;

/// <summary>
/// Thrown in place of a transmission while listen-only mode is on. Nothing was sent.
/// </summary>
public sealed class TransmitBlockedException : NetControlException
{
    public TransmitBlockedException(TransmitKind kind, string destination, string purpose)
        : base($"Listen-only mode is on, so nothing was sent ({Describe(kind)} to {destination} for {purpose}).")
    {
        Kind = kind;
        Destination = destination;
        Purpose = purpose;
        Remediation = "Turn Listen only off in the toolbar if you mean this tool to transmit.";
    }

    public TransmitKind Kind { get; }

    public string Destination { get; }

    public string Purpose { get; }

    /// <summary>A transmission kind in plain words.</summary>
    public static string Describe(TransmitKind kind) => kind switch
    {
        TransmitKind.DhcpReply => "a BOOTP/DHCP reply",
        TransmitKind.ListIdentity => "an EtherNet/IP identity request",
        TransmitKind.TcpConnect => "a TCP connection",
        TransmitKind.Ping => "a ping",
        TransmitKind.Tftp => "a TFTP packet",
        TransmitKind.RawFrame => "a PROFINET frame",
        TransmitKind.AdapterChange => "a change to this PC's adapter",
        _ => kind.ToString(),
    };
}
