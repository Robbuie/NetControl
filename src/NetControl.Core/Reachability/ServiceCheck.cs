namespace NetControl.Core.Reachability;

/// <summary>One port, and what came back.</summary>
public sealed record ServiceCheck(ServicePort Service, ServiceState State, TimeSpan Elapsed, string? Detail = null)
{
    public bool IsOpen => State == ServiceState.Open;

    /// <summary>Short enough for a list cell.</summary>
    public string StateText => State switch
    {
        ServiceState.Open => "open",
        ServiceState.Refused => "refused",
        ServiceState.NoAnswer => "no answer",
        _ => "failed",
    };
}
