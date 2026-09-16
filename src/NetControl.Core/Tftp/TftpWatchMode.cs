namespace NetControl.Core.Tftp;

/// <summary>
/// What the watch server does with a request once it has recorded it.
///
/// <para>The split exists for the same reason <c>DhcpServerMode</c>'s does. Recording what a
/// controller asked for answers the question completely and costs nothing: the first packet
/// carries the filename, the mode and the options, which is everything anybody needs to know.
/// Accepting the transfer means becoming the backup server for twenty minutes and writing a
/// couple of hundred megabytes into somebody's plant, which is a much larger thing to do and a
/// much larger thing to get wrong.</para>
/// </summary>
public enum TftpWatchMode
{
    /// <summary>
    /// Record the request and refuse it, transferring nothing. The default, and the only mode
    /// that is built.
    /// </summary>
    Observe = 0,

    /// <summary>
    /// Receive the file into a folder the user chose.
    ///
    /// <para><b>Not built.</b> <see cref="TftpWatchServer"/> refuses to start in this mode rather
    /// than silently behaving like <see cref="Observe"/> - a tool that says it is accepting
    /// backups and is not would be worse than no tool. The member is declared because the
    /// distinction shapes the type, in the same way <c>DeviceState.Verified</c> existed before
    /// anything set it.</para>
    /// </summary>
    Accept = 1,
}
