namespace NetControl.Core.Reachability;

/// <summary>
/// A ping or a service check was refused before anything was sent - nearly always because the
/// target was not one host.
///
/// <para>Not used for a host that did not answer. Silence is a result, and it is reported as one;
/// this is for the cases where sending at all would have been the mistake.</para>
/// </summary>
public sealed class ReachabilityException : NetControlException
{
    public ReachabilityException(string message)
        : base(message)
    {
    }

    public ReachabilityException(string message, Exception? inner)
        : base(message, inner)
    {
    }
}
