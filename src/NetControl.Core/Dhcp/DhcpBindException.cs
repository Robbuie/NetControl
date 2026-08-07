using NetControl.Core.Interfaces;

namespace NetControl.Core.Dhcp;

/// <summary>
/// The server could not take the port, or took it in circumstances where it could not do its job
/// honestly. Carries the port-ownership finding so the UI can name the process rather than
/// showing a socket error code.
/// </summary>
public sealed class DhcpBindException : DhcpException
{
    public DhcpBindException(string message, PortConflictReport? conflict = null, Exception? inner = null)
        : base(message, inner)
    {
        Conflict = conflict;
    }

    public PortConflictReport? Conflict { get; }
}
