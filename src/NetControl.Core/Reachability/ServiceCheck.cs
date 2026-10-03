namespace NetControl.Core.Reachability;

/// <summary>
/// One port, and what came back - and, when the port is open and a handshake exists for it, what
/// the protocol behind it said.
/// </summary>
public sealed record ServiceCheck(
    ServicePort Service,
    ServiceState State,
    TimeSpan Elapsed,
    string? Detail = null,
    ProtocolVerdict? Protocol = null)
{
    public bool IsOpen => State == ServiceState.Open;

    /// <summary>Open, and the handshake proved the protocol the port is named for.</summary>
    public bool IsVerified => IsOpen && Protocol is { Confirmed: true };

    /// <summary>Open, a handshake was tried, and it did not prove the protocol.</summary>
    public bool IsUnverified => IsOpen && Protocol is { Confirmed: false };

    /// <summary>Short enough for a list cell.</summary>
    public string StateText => State switch
    {
        ServiceState.Open when IsVerified => "verified",
        ServiceState.Open when IsUnverified => "open, unproven",
        ServiceState.Open => "open",
        ServiceState.Refused => "refused",
        ServiceState.NoAnswer => "no answer",
        _ => "failed",
    };

    /// <summary>The handshake's sentence when there is one, otherwise what the port usually means.</summary>
    public string DetailText => Protocol?.Text ?? Service.Meaning;
}
