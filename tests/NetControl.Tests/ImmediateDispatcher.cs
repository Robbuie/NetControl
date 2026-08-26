using NetControl.App.Composition;

namespace NetControl.Tests;

/// <summary>
/// Runs everything inline on the calling thread.
///
/// This is why <see cref="IUiDispatcher"/> exists. The real one wraps the WPF dispatcher, which
/// needs a message pump; a test of how the log collapses a retransmit wants to raise an event and
/// then assert, with nothing in between.
/// </summary>
internal sealed class ImmediateDispatcher : IUiDispatcher
{
    public bool IsOnUiThread => true;

    public void Post(Action action) => action();
}
