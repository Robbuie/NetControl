using NetControl.Core.Interfaces;

namespace NetControl.Core.Tftp;

/// <summary>
/// The watch server could not take UDP/69, or could have taken it only in circumstances where it
/// could not do its job honestly. Carries the port-ownership finding so the UI can name the
/// process rather than showing a socket error code.
/// </summary>
public sealed class TftpBindException : TftpException
{
    public TftpBindException(string message, PortConflictReport? conflict = null, Exception? inner = null)
        : base(message, inner)
    {
        Conflict = conflict;
    }

    public PortConflictReport? Conflict { get; }
}
