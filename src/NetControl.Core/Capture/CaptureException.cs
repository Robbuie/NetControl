namespace NetControl.Core.Capture;

/// <summary>An adapter could not be opened for capture, or a frame could not be sent.</summary>
public sealed class CaptureException : NetControlException
{
    public CaptureException(string message)
        : base(message)
    {
    }

    public CaptureException(string message, Exception? inner)
        : base(message, inner)
    {
    }
}
