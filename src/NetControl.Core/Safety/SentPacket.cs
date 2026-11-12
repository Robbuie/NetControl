namespace NetControl.Core.Safety;

/// <summary>
/// One entry in the sent-packet ledger: something the application put on the wire, or would have
/// and was stopped.
/// </summary>
/// <param name="Utc">When.</param>
/// <param name="Kind">What kind of transmission.</param>
/// <param name="Destination">Where it went - an address, an address and port, or "broadcast".</param>
/// <param name="Purpose">Why, in the words the feature uses: "Scan", "Set static", "reply to 00:1D:...".</param>
/// <param name="Bytes">Payload size when known, otherwise 0.</param>
/// <param name="Blocked">True when listen-only mode stopped it. Nothing was sent.</param>
public sealed record SentPacket(
    DateTimeOffset Utc,
    TransmitKind Kind,
    string Destination,
    string Purpose,
    int Bytes,
    bool Blocked);
