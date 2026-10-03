namespace NetControl.Core.Capture;

/// <summary>
/// Raw Ethernet frames in and out of one adapter. The only thing in the engine that sees below IP,
/// and the only thing that needs a capture driver - which is why it is an interface: the passive
/// inventory and PROFINET DCP are written and tested against this, and the Npcap implementation is
/// a thin shim that is loaded only when the driver is installed.
/// </summary>
public interface IFrameChannel : IDisposable
{
    /// <summary>The adapter's own MAC, which every frame this tool sends goes out from.</summary>
    MacAddress LocalMac { get; }

    /// <summary>Which adapter, for the record and the status line.</summary>
    string AdapterName { get; }

    /// <summary>Raised on the capture thread for every frame the filter lets through.</summary>
    event EventHandler<FrameEventArgs>? FrameReceived;

    /// <summary>Raised once if capture stops by itself - the adapter went away, or the driver failed.</summary>
    event EventHandler<string>? Faulted;

    /// <summary>Sends one frame exactly as given, padded to the Ethernet minimum.</summary>
    void Send(byte[] frame);
}
