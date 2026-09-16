namespace NetControl.Core.Tftp;

/// <summary>
/// One RFC 2347 option, exactly as it appeared on the wire.
///
/// <para>The value stays a string. Every option this protocol has ever defined carries a number,
/// but a device that sends <c>blksize=1428bytes</c> is a device we want to be able to show
/// somebody, and a parse straight to <c>int</c> would turn it into either a zero or an
/// exception. The typed reads live on <see cref="TftpOptions"/> and are all nullable for the
/// same reason.</para>
/// </summary>
/// <param name="Name">As sent. Option names are case-insensitive per RFC 2347; the case is kept.</param>
/// <param name="Value">As sent, un-parsed and un-trimmed.</param>
public sealed record TftpOption(string Name, string Value)
{
    /// <summary>RFC 2348. The one that decides whether a large image can cross at all.</summary>
    public const string BlockSizeName = "blksize";

    /// <summary>RFC 2349.</summary>
    public const string TimeoutName = "timeout";

    /// <summary>RFC 2349. Zero in a read request means "tell me how big it is".</summary>
    public const string TransferSizeName = "tsize";

    /// <summary>RFC 7440.</summary>
    public const string WindowSizeName = "windowsize";

    public bool Is(string name) => string.Equals(Name, name, StringComparison.OrdinalIgnoreCase);

    public override string ToString() => $"{Name}={Value}";
}
