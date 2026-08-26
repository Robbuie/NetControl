using System.Windows.Threading;

namespace NetControl.App.Composition;

/// <summary>
/// The real <see cref="IUiDispatcher"/>, over the WPF dispatcher.
/// </summary>
public sealed class WpfDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    private readonly Dispatcher _dispatcher = dispatcher
        ?? throw new ArgumentNullException(nameof(dispatcher));

    public bool IsOnUiThread => _dispatcher.CheckAccess();

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            // The window is going away and the server is still draining. Dropping the update is
            // correct; queuing it would throw on a background thread during shutdown.
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }

        // BeginInvoke, never Invoke. Invoke would park the DHCP receive loop until the UI thread
        // got round to it, and a receive loop that is not reading the socket is a tool that
        // misses requests - the exact failure this product exists to remove.
        _dispatcher.BeginInvoke(DispatcherPriority.Background, action);
    }
}
