namespace NetControl.Core.Dhcp;

/// <summary>
/// Something went wrong that the user needs to know about. The message names the likely cause and
/// the remediation names the next action — a bare error code is not a product.
/// </summary>
public sealed class DhcpFaultEventArgs(
    DateTimeOffset timestamp,
    string message,
    string? remediation = null,
    Exception? exception = null,
    bool isFatal = false) : EventArgs()
{
    public DateTimeOffset Timestamp { get; } = timestamp;

    public string Message { get; } = message;

    public string? Remediation { get; } = remediation;

    public Exception? Exception { get; } = exception;

    /// <summary>True when the server has stopped. The UI must not keep showing a green light.</summary>
    public bool IsFatal { get; } = isFatal;
}
