namespace NetControl.Core.Capture;

/// <summary>One Ethernet frame off the wire, starting at the destination MAC, without the FCS.</summary>
public sealed class FrameEventArgs(byte[] frame, DateTimeOffset utc) : EventArgs
{
    public byte[] Frame { get; } = frame;

    public DateTimeOffset Utc { get; } = utc;
}
