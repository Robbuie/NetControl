namespace NetControl.Core.Reachability;

/// <summary>
/// What a protocol handshake established about an open port.
///
/// <para><see cref="Confirmed"/> is the whole point of the type: true only when the reply could
/// have come from nothing but the protocol named. "It answered with something else" and "it said
/// nothing" are both false, and both are worth a sentence, because each points somewhere different -
/// a different service squatting on a well-known port, or a device that accepts connections it
/// does not serve.</para>
/// </summary>
/// <param name="Confirmed">True when the reply proves <paramref name="Protocol"/> is there.</param>
/// <param name="Protocol">The protocol that was asked about.</param>
/// <param name="Text">What the reply said: a product name, a model, a server header, or what went wrong.</param>
public sealed record ProtocolVerdict(bool Confirmed, string Protocol, string Text)
{
    public static ProtocolVerdict Yes(string protocol, string text) => new(true, protocol, text);

    public static ProtocolVerdict Other(string protocol, string text) => new(false, protocol, text);

    /// <summary>Connected, asked, and heard nothing back before the timeout.</summary>
    public static ProtocolVerdict Silent(string protocol, TimeSpan waited) =>
        new(false, protocol, $"accepted the connection but did not answer the {protocol} request within {waited.TotalSeconds:0} s");

    public override string ToString() => Confirmed ? $"{Protocol}: {Text}" : $"not {Protocol}: {Text}";
}
