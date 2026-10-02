namespace NetControl.Core.Reachability;

/// <summary>
/// What one TCP connect said. Three answers, kept apart, because they mean three different things.
/// </summary>
public enum ServiceState
{
    /// <summary>The connection was accepted. Something is listening.</summary>
    Open,

    /// <summary>
    /// Actively refused. Nothing is listening on that port - but something at that address is up
    /// and answered, which is itself worth knowing.
    /// </summary>
    Refused,

    /// <summary>Nothing came back in time: filtered by a firewall, or nothing there at all.</summary>
    NoAnswer,

    /// <summary>The connect failed for another reason. The result's detail says which.</summary>
    Failed,
}
