using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NetControl.Core.Capture;

/// <summary>
/// The dozen wpcap.dll entry points this tool uses, resolved at run time from Npcap's own folder.
///
/// <para><b>Not a DllImport, on purpose.</b> A <c>[DllImport("wpcap.dll")]</c> binds when first
/// called and throws <see cref="DllNotFoundException"/> on every machine without Npcap - and the
/// safety rule is that nothing in a core path may depend on the driver. Loading by path, once, and
/// only when somebody opens the Passive or PROFINET tab, means a machine without Npcap never so much
/// as looks for the file.</para>
///
/// <para>Everything crosses as <see cref="IntPtr"/>: strings are marshalled by hand to ANSI, the
/// way libpcap wants them, and nothing here needs unsafe code.</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class NpcapNative
{
    private static readonly Lazy<(NpcapNative? Native, string? Problem)> Loaded = new(Load);

    private NpcapNative(IntPtr wpcap)
    {
        OpenLive = Get<OpenLiveFn>(wpcap, "pcap_open_live");
        NextEx = Get<NextExFn>(wpcap, "pcap_next_ex");
        SendPacket = Get<SendPacketFn>(wpcap, "pcap_sendpacket");
        Close = Get<CloseFn>(wpcap, "pcap_close");
        GetErr = Get<GetErrFn>(wpcap, "pcap_geterr");
        Compile = Get<CompileFn>(wpcap, "pcap_compile");
        SetFilter = Get<SetFilterFn>(wpcap, "pcap_setfilter");
        FreeCode = Get<FreeCodeFn>(wpcap, "pcap_freecode");
        LibVersion = Get<LibVersionFn>(wpcap, "pcap_lib_version");
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr OpenLiveFn(IntPtr device, int snaplen, int promisc, int timeoutMs, IntPtr errbuf);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int NextExFn(IntPtr handle, out IntPtr header, out IntPtr data);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int SendPacketFn(IntPtr handle, byte[] buffer, int size);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void CloseFn(IntPtr handle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr GetErrFn(IntPtr handle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int CompileFn(IntPtr handle, IntPtr program, IntPtr filter, int optimize, uint netmask);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int SetFilterFn(IntPtr handle, IntPtr program);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void FreeCodeFn(IntPtr program);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr LibVersionFn();

    /// <summary>libpcap's PCAP_ERRBUF_SIZE.</summary>
    public const int ErrorBufferSize = 256;

    public OpenLiveFn OpenLive { get; }

    public NextExFn NextEx { get; }

    public SendPacketFn SendPacket { get; }

    public CloseFn Close { get; }

    public GetErrFn GetErr { get; }

    public CompileFn Compile { get; }

    public SetFilterFn SetFilter { get; }

    public FreeCodeFn FreeCode { get; }

    public LibVersionFn LibVersion { get; }

    /// <summary>The folder Npcap installs its DLLs into - deliberately not System32 itself.</summary>
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Npcap");

    /// <summary>The loaded library, or null and why not. Loaded once per process.</summary>
    public static (NpcapNative? Native, string? Problem) Instance => Loaded.Value;

    public static string Text(IntPtr ansi) => ansi == IntPtr.Zero ? string.Empty : Marshal.PtrToStringAnsi(ansi) ?? string.Empty;

    private static (NpcapNative?, string?) Load()
    {
        string wpcap = Path.Combine(Folder, "wpcap.dll");
        string packet = Path.Combine(Folder, "Packet.dll");

        if (!File.Exists(wpcap))
        {
            return (null, null);
        }

        // Packet.dll first, from the same folder: wpcap imports it by bare name, and the folder is not
        // on the DLL search path - Npcap keeps it out of System32 on purpose, so it cannot be
        // confused with an old WinPcap.
        if (File.Exists(packet))
        {
            NativeLibrary.TryLoad(packet, out _);
        }

        if (!NativeLibrary.TryLoad(wpcap, out IntPtr handle))
        {
            return (null, $"{wpcap} is there but could not be loaded - a 32-bit copy, or a damaged install.");
        }

        try
        {
            return (new NpcapNative(handle), null);
        }
        catch (EntryPointNotFoundException ex)
        {
            return (null, $"{wpcap} is missing {ex.Message} - an Npcap older than this tool understands.");
        }
    }

    private static T Get<T>(IntPtr library, string name)
        where T : Delegate =>
        NativeLibrary.TryGetExport(library, name, out IntPtr address)
            ? Marshal.GetDelegateForFunctionPointer<T>(address)
            : throw new EntryPointNotFoundException(name);
}
