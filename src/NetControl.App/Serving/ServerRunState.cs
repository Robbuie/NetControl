namespace NetControl.App.Serving;

/// <summary>
/// Where the listener is in its life. Four states rather than a boolean because the UI has to
/// distinguish "stopping" from "stopped" (the button must not re-arm mid-teardown) and "faulted"
/// from "stopped" (a green light must not survive a socket that died under us).
/// </summary>
public enum ServerRunState
{
    Stopped = 0,
    Starting = 1,
    Listening = 2,
    Stopping = 3,

    /// <summary>The listener stopped on its own, because of a fault. Never shows as ready.</summary>
    Faulted = 4,
}
