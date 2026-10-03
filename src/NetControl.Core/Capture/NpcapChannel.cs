using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NetControl.Core.Capture;

/// <summary>
/// One adapter opened through Npcap: a background thread reading frames, and a send.
///
/// <para>The read loop runs with a 250 ms driver timeout so that Dispose is never more than a
/// quarter of a second away, and the handle is closed only after the thread has stopped - closing a
/// pcap handle out from under <c>pcap_next_ex</c> is a crash, not an error.</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class NpcapChannel : IFrameChannel
{
    private const int SnapLength = 2048;
    private const int ReadTimeoutMs = 250;
    private const int MinimumFrame = 60;

    /// <summary>libpcap's PCAP_NETMASK_UNKNOWN; only matters for filters that test broadcast.</summary>
    private const uint NetmaskUnknown = 0xFFFFFFFF;

    private readonly NpcapNative _native;
    private readonly IntPtr _handle;
    private readonly Thread _reader;
    private readonly Lock _sendGate = new();
    private volatile bool _stopping;
    private bool _disposed;

    private NpcapChannel(NpcapNative native, IntPtr handle, MacAddress localMac, string adapterName)
    {
        _native = native;
        _handle = handle;
        LocalMac = localMac;
        AdapterName = adapterName;
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = $"capture {adapterName}" };
    }

    public event EventHandler<FrameEventArgs>? FrameReceived;

    public event EventHandler<string>? Faulted;

    public MacAddress LocalMac { get; }

    public string AdapterName { get; }

    /// <exception cref="CaptureException">The adapter would not open, or the filter would not compile.</exception>
    public static NpcapChannel Open(NpcapNative native, string device, MacAddress localMac, string adapterName, string filter, bool promiscuous)
    {
        IntPtr error = Marshal.AllocHGlobal(NpcapNative.ErrorBufferSize);
        IntPtr deviceName = Marshal.StringToHGlobalAnsi(device);

        try
        {
            Marshal.WriteByte(error, 0);
            IntPtr handle = native.OpenLive(deviceName, SnapLength, promiscuous ? 1 : 0, ReadTimeoutMs, error);

            if (handle == IntPtr.Zero)
            {
                string why = NpcapNative.Text(error);
                throw new CaptureException($"Npcap could not open {adapterName}: {why}")
                {
                    Remediation = why.Contains("denied", StringComparison.OrdinalIgnoreCase)
                        || why.Contains("administrator", StringComparison.OrdinalIgnoreCase)
                        ? "Npcap is installed for administrators only. Run NetControl as administrator, or reinstall Npcap "
                            + "with \"Restrict Npcap driver's access to Administrators only\" unticked."
                        : "Check the adapter is enabled and connected, then try again. Restarting the Npcap service, or the PC, "
                            + "clears a driver that has lost track of an adapter.",
                };
            }

            var channel = new NpcapChannel(native, handle, localMac, adapterName);

            try
            {
                channel.ApplyFilter(filter);
            }
            catch
            {
                native.Close(handle);
                throw;
            }

            channel._reader.Start();
            return channel;
        }
        finally
        {
            Marshal.FreeHGlobal(deviceName);
            Marshal.FreeHGlobal(error);
        }
    }

    public void Send(byte[] frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(_disposed, this);

        byte[] padded = frame.Length >= MinimumFrame ? frame : [.. frame, .. new byte[MinimumFrame - frame.Length]];

        lock (_sendGate)
        {
            if (_native.SendPacket(_handle, padded, padded.Length) != 0)
            {
                throw new CaptureException($"Npcap could not send on {AdapterName}: {NpcapNative.Text(_native.GetErr(_handle))}")
                {
                    Remediation = "Check the adapter is still connected.",
                };
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopping = true;

        if (_reader.IsAlive && Thread.CurrentThread != _reader)
        {
            _reader.Join(TimeSpan.FromSeconds(2));
        }

        _native.Close(_handle);
    }

    private void ApplyFilter(string filter)
    {
        // struct bpf_program { u_int bf_len; struct bpf_insn *bf_insns; } - 16 bytes on x64 with
        // the pointer aligned to 8. Allocated, not declared, so nothing here needs unsafe code.
        IntPtr program = Marshal.AllocHGlobal(16);
        IntPtr text = Marshal.StringToHGlobalAnsi(filter);

        try
        {
            if (_native.Compile(_handle, program, text, 1, NetmaskUnknown) != 0)
            {
                throw new CaptureException($"The capture filter '{filter}' did not compile: {NpcapNative.Text(_native.GetErr(_handle))}");
            }

            try
            {
                if (_native.SetFilter(_handle, program) != 0)
                {
                    throw new CaptureException($"Npcap refused the capture filter: {NpcapNative.Text(_native.GetErr(_handle))}");
                }
            }
            finally
            {
                _native.FreeCode(program);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(text);
            Marshal.FreeHGlobal(program);
        }
    }

    private void ReadLoop()
    {
        while (!_stopping)
        {
            int result = _native.NextEx(_handle, out IntPtr header, out IntPtr data);

            if (result == 1)
            {
                // struct pcap_pkthdr { struct timeval ts; bpf_u_int32 caplen; bpf_u_int32 len; }.
                // On Windows a timeval is two 32-bit longs, so caplen sits at offset 8.
                int length = Marshal.ReadInt32(header, 8);
                if (length <= 0 || length > SnapLength)
                {
                    continue;
                }

                var frame = new byte[length];
                Marshal.Copy(data, frame, 0, length);

                try
                {
                    FrameReceived?.Invoke(this, new FrameEventArgs(frame, DateTimeOffset.UtcNow));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // A subscriber's bug must not stop capture. It is reported once and the loop goes on.
                    Faulted?.Invoke(this, $"A frame handler failed: {ex.Message}");
                }
            }
            else if (result < 0)
            {
                if (!_stopping)
                {
                    Faulted?.Invoke(this, $"Capture on {AdapterName} stopped: {NpcapNative.Text(_native.GetErr(_handle))}");
                }

                return;
            }
        }
    }
}
