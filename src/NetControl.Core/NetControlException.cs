namespace NetControl.Core;

/// <summary>
/// Base for every exception this engine raises deliberately.
///
/// The rule that matters is in the message, not the type: a failure the user can see must
/// name the likely cause and the next action. "Bind failed" is useless; "UDP/67 is held by
/// vmnetdhcp.exe (pid 4312) - stop that service and retry" is the product.
/// </summary>
public abstract class NetControlException : Exception
{
    protected NetControlException(string message) : base(message) { }

    protected NetControlException(string message, Exception? inner) : base(message, inner) { }

    /// <summary>
    /// What the user should do next, in one sentence. The UI shows this beneath the message.
    /// Null only when there genuinely is no useful next step.
    /// </summary>
    public string? Remediation { get; init; }
}
