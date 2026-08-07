using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NetControl.Core.Interfaces;

/// <summary>
/// The one P/Invoke the Phase 1 engine needs.
///
/// <see cref="System.Net.NetworkInformation.IPGlobalProperties.GetActiveUdpListeners"/> already
/// lists bound endpoints, but it does not report the owning process — and naming the process is
/// the entire value of the check. "UDP/67 is in use" sends the user hunting; "vmnetdhcp.exe
/// (pid 4312) holds it" ends the hunt.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class NativeMethods
{
    internal const int AF_INET = 2;

    /// <summary>UDP_TABLE_CLASS.UDP_TABLE_OWNER_PID</summary>
    internal const int UDP_TABLE_OWNER_PID = 1;

    internal const uint NO_ERROR = 0;
    internal const uint ERROR_INSUFFICIENT_BUFFER = 122;

    /// <summary>
    /// MIB_UDPROW_OWNER_PID is { DWORD dwLocalAddr; DWORD dwLocalPort; DWORD dwOwningPid; }.
    /// The table is a DWORD count followed by that many rows.
    /// </summary>
    internal const int UdpRowOwnerPidSize = 12;

    // DllImport rather than LibraryImport on purpose: the source generator emits unsafe code and
    // would force AllowUnsafeBlocks across the whole engine for one call. See the SYSLIB1054 note
    // in .editorconfig.
    [DllImport("iphlpapi.dll", SetLastError = true)]
    internal static extern uint GetExtendedUdpTable(
        IntPtr pUdpTable,
        ref int dwOutBufLen,
        [MarshalAs(UnmanagedType.Bool)] bool sort,
        int ipVersion,
        int tableClass,
        int reserved);
}
