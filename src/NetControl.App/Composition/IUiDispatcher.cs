namespace NetControl.App.Composition;

/// <summary>
/// Moves work onto the UI thread.
///
/// This exists so the view models can stay free of WPF types. <c>NetControl.Core</c> raises every
/// event on the DHCP receive loop's thread, so something has to marshal, and if that something
/// were <see cref="System.Windows.Threading.Dispatcher"/> used directly then a test of the log's
/// retransmit collapsing would need a message pump to run.
/// </summary>
public interface IUiDispatcher
{
    /// <summary>True when the caller is already on the UI thread and can touch bound state.</summary>
    bool IsOnUiThread { get; }

    /// <summary>
    /// Queues work and returns immediately. Never blocks - the caller is usually the receive
    /// loop, and a loop parked on the UI thread is a loop that is not reading the socket.
    /// </summary>
    void Post(Action action);
}
