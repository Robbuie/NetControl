using NetControl.Core.Interfaces;

namespace NetControl.Core.Capture;

/// <summary>Where frame channels come from. <see cref="NpcapProvider"/> on a real machine; a fake in tests.</summary>
public interface ICaptureProvider
{
    /// <summary>Whether a channel can be opened at all. Cheap; safe to call at startup.</summary>
    CaptureAvailability Check();

    /// <summary>
    /// Opens one adapter. <paramref name="filter"/> is a BPF expression applied in the driver, so
    /// frames nobody asked for never cross into managed code.
    /// </summary>
    /// <exception cref="CaptureException">With the reason and the next step.</exception>
    IFrameChannel Open(NicInfo nic, string filter, bool promiscuous);
}
