using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NetControl.Core.Interfaces;

/// <summary>
/// The machine's IPv4 ARP cache, which is how a device found by a scan gets a MAC.
///
/// <para>A ListIdentity reply carries an address and no hardware address at all - the identity
/// object simply does not have one. The plan, the request log and the retransmit filter are all
/// keyed on MAC. The join between them is this table, and the reason it works is timing: the reply
/// that just arrived is what populated the entry, so reading the cache immediately after a scan
/// finds the mapping already there for free, with nothing extra put on the wire.</para>
///
/// <para>It is a cache, so it can miss - an entry ages out, or a reply came through a router and
/// the hardware address on this side of it belongs to the router rather than the device. A miss is
/// reported as a miss. The fallback, reading Ethernet Link object 0xF6 attribute 3, needs a TCP
/// session to the device and belongs with the commissioner rather than here.</para>
/// </summary>
public sealed class ArpTable : IArpLookup
{
    public bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>
    /// Every usable entry, or an empty list on a platform that has no table to read. Callers tell
    /// the two apart with <see cref="IsSupported"/>, because "nothing there" and "did not look"
    /// lead to different next actions.
    /// </summary>
    public IReadOnlyList<ArpEntry> Snapshot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        return Read();
    }

    /// <summary>
    /// The whole table, invalid rows and all. <see cref="Snapshot"/> is what callers want; this is
    /// here because a diagnostic that hides the invalid rows cannot explain why an address the
    /// user can ping produced no MAC.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<ArpEntry> Read()
    {
        var entries = new List<ArpEntry>();

        int size = 0;
        uint rc = NativeMethods.GetIpNetTable(IntPtr.Zero, ref size, false);

        if (rc == NativeMethods.ERROR_NO_DATA || size == 0)
        {
            return entries;
        }

        if (rc != NativeMethods.ERROR_INSUFFICIENT_BUFFER && rc != NativeMethods.NO_ERROR)
        {
            return entries;
        }

        IntPtr table = Marshal.AllocHGlobal(size);

        try
        {
            rc = NativeMethods.GetIpNetTable(table, ref size, false);
            if (rc != NativeMethods.NO_ERROR)
            {
                return entries;
            }

            int count = Marshal.ReadInt32(table);
            IntPtr row = table + sizeof(int);

            for (int i = 0; i < count; i++, row += NativeMethods.IpNetRowSize)
            {
                entries.Add(ReadRow(row));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(table);
        }

        return entries;
    }

    [SupportedOSPlatform("windows")]
    private static ArpEntry ReadRow(IntPtr row)
    {
        int interfaceIndex = Marshal.ReadInt32(row);
        int physicalLength = Marshal.ReadInt32(row + 4);

        var physical = new byte[NativeMethods.MaxPhysicalAddressLength];
        Marshal.Copy(row + NativeMethods.IpNetRowPhysAddrOffset, physical, 0, physical.Length);

        // dwAddr is already in network order, which is the order IPAddress wants. Same rule as the
        // UDP table beside it: read it deliberately rather than reversing it out of habit.
        uint address = (uint)Marshal.ReadInt32(row + NativeMethods.IpNetRowAddressOffset);
        int type = Marshal.ReadInt32(row + NativeMethods.IpNetRowTypeOffset);

        // Anything that is not a six-byte Ethernet address - a tunnel, a loopback pseudo-entry -
        // has no MAC worth putting in a plan, so it comes back empty rather than truncated.
        MacAddress mac = physicalLength == MacAddress.Length ? new MacAddress(physical) : MacAddress.Empty;

        return new ArpEntry(
            new IPAddress(BitConverter.GetBytes(address)),
            mac,
            interfaceIndex,
            Enum.IsDefined((ArpEntryType)type) ? (ArpEntryType)type : ArpEntryType.Other);
    }
}
