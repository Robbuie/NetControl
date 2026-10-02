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
    /// Record the request and refuse it, transferring nothing. The default.
    /// </summary>
    Observe = 0,

    /// <summary>
    /// Receive the file into a folder the user chose, and send a file in that folder back when one
    /// is asked for.
    ///
    /// <para>Its purpose is not to replace the backup server. It is to prove the network and the
    /// controller are fine when the server's configuration is not: a backup that lands here and fails
    /// against the real server has been narrowed to the server in one run. It never writes outside
    /// the folder, never over an existing file unless told to, and takes one transfer at a time -
    /// see <see cref="TftpAcceptPath"/> and <see cref="TftpWatchOptions.AllowOverwrite"/>.</para>
    /// </summary>
    Accept = 1,
}
