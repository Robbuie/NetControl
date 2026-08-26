namespace NetControl.Core.Oui;

/// <summary>
/// The packed OUI table could not be read.
///
/// In practice this only fires when the embedded resource is missing or was produced by a
/// different version of the packer, both of which are build mistakes rather than field problems.
/// It is still a typed exception with a remediation because the alternative - a corrupt file
/// silently yielding wrong vendor names - is the kind of thing nobody notices until a
/// commissioning report blames the wrong manufacturer.
/// </summary>
public sealed class OuiException : NetControlException
{
    public OuiException(string message) : base(message) { }

    public OuiException(string message, Exception? inner) : base(message, inner) { }
}
