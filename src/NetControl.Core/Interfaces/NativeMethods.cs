using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NetControl.Core.Interfaces;

/// <summary>
/// The two P/Invokes the engine needs.
///
/// <para><see cref="System.Net.NetworkInformation.IPGlobalProperties.GetActiveUdpListeners"/>
/// already lists bound endpoints, but it does not report the owning process - and naming the
/// process is the entire value of the check. "UDP/67 is in use" sends the user hunting;
/// "vmnetdhcp.exe (pid 4312) holds it" ends the hunt.</para>
///
/// <para>The ARP table has no managed equivalent at all, and without it a device found by a scan
/// cannot become a plan row: a ListIdentity reply carries an address, the plan is keyed on MAC,
/// and the only thing that maps one to the other is the cache the reply has just populated.</para>
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

    /// <summary>An ARP table with no entries in it at all - a fresh boot, or an adapter just up.</summary>
    internal const uint ERROR_NO_DATA = 232;

    /// <summary>
    /// MIB_IPNETROW is { DWORD dwIndex; DWORD dwPhysAddrLen; BYTE bPhysAddr[8]; DWORD dwAddr;
    /// DWORD dwType; }, and the table is a DWORD count followed by that many rows.
    /// </summary>
    internal const int IpNetRowSize = 24;

    internal const int IpNetRowPhysAddrOffset = 8;
    internal const int IpNetRowAddressOffset = 16;
    internal const int IpNetRowTypeOffset = 20;

    /// <summary>MAXLEN_PHYSADDR. Ethernet uses six of the eight.</summary>
    internal const int MaxPhysicalAddressLength = 8;

    /// <summary>
    /// The IPv4 ARP cache.
    ///
    /// <para>GetIpNetTable2 is the modern call and it is the one Microsoft points at, but its
    /// MIB_IPNET_ROW2 embeds a SOCKADDR_INET union and a NET_LUID, so hand-marshalling it means
    /// hand-computing alignment - and getting that wrong reads plausible-looking garbage rather
    /// than failing. This one is a flat 24-byte row with no unions in it, it is IPv4-only which is
    /// all this tool is, and it follows the same allocate-measure-allocate shape as
    /// <see cref="GetExtendedUdpTable"/> immediately above.</para>
    /// </summary>
    [DllImport("iphlpapi.dll", SetLastError = true)]
    internal static extern uint GetIpNetTable(
        IntPtr pIpNetTable,
        ref int pdwSize,
        [MarshalAs(UnmanagedType.Bool)] bool bOrder);
}
