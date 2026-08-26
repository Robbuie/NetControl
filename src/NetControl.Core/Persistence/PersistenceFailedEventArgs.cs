namespace NetControl.Core.Persistence;

/// <summary>
/// A row that could not be written to the project.
///
/// This is a notification, not an exception, because the alternative - letting a database problem
/// propagate out of a handler on the DHCP receive loop - would stop the tool answering devices in
/// order to complain about the paperwork. The right behaviour is to keep serving and tell the user
/// the record is incomplete.
/// </summary>
/// <param name="what">Which kind of row: "request", "reply", "fault", "server start".</param>
/// <param name="exception">What went wrong. A <see cref="PersistenceException"/> carries a remediation.</param>
public sealed class PersistenceFailedEventArgs(string what, Exception exception) : EventArgs()
{
    public string What { get; } = what;

    public Exception Exception { get; } = exception;

    /// <summary>The remediation if there is one, otherwise the exception message.</summary>
    public string Message => Exception is NetControlException { Remediation: { } remediation }
        ? $"{Exception.Message} {remediation}"
        : Exception.Message;
}
