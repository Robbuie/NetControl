using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NetControl.Spike.BootpListen;

/// <summary>
/// Finds out who already owns UDP/67. This is the single most common reason a BOOTP tool
/// appears to start fine and then never receives anything: VMware, VirtualBox, WSL/Hyper-V,
/// Docker, or a second copy of the tool is holding the port. Windows lets both bind when
/// SO_REUSEADDR is in play, so you get silence rather than an error.
/// </summary>
[SupportedOSPlatform("windows")]
public static class PortOwner
{
    private const int AF_INET = 2;
    private const int UDP_TABLE_OWNER_PID = 1;
    private const uint ERROR_INSUFFICIENT_BUFFER = 122;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(
        IntPtr pUdpTable, ref int dwOutBufLen, bool sort,
        int ipVersion, int tableClass, int reserved);

    public readonly record struct Listener(string LocalAddress, int Port, int Pid, string ProcessName);

    public static List<Listener> FindListenersOnPort(int port)
    {
        var results = new List<Listener>();
        int size = 0;
        uint rc = GetExtendedUdpTable(IntPtr.Zero, ref size, false, AF_INET, UDP_TABLE_OWNER_PID, 0);
        if (rc != ERROR_INSUFFICIENT_BUFFER && rc != 0) return results;

        IntPtr table = Marshal.AllocHGlobal(size);
        try
        {
            rc = GetExtendedUdpTable(table, ref size, false, AF_INET, UDP_TABLE_OWNER_PID, 0);
            if (rc != 0) return results;

            int count = Marshal.ReadInt32(table);
            IntPtr row = table + 4;

            // MIB_UDPROW_OWNER_PID { DWORD dwLocalAddr; DWORD dwLocalPort; DWORD dwOwningPid; }
            const int rowSize = 12;
            for (int i = 0; i < count; i++, row += rowSize)
            {
                uint localAddr = (uint)Marshal.ReadInt32(row);
                uint localPortRaw = (uint)Marshal.ReadInt32(row + 4);
                int pid = Marshal.ReadInt32(row + 8);

                // dwLocalPort is network byte order in the low two bytes.
                var portBytes = BitConverter.GetBytes(localPortRaw);
                int localPort = BinaryPrimitives.ReadUInt16BigEndian(portBytes);
                if (localPort != port) continue;

                string name;
                try { name = Process.GetProcessById(pid).ProcessName; }
                catch { name = "(exited or access denied)"; }

                var addrBytes = BitConverter.GetBytes(localAddr);
                results.Add(new Listener($"{addrBytes[0]}.{addrBytes[1]}.{addrBytes[2]}.{addrBytes[3]}",
                                         localPort, pid, name));
            }
        }
        finally { Marshal.FreeHGlobal(table); }

        return results;
    }

    /// <summary>
    /// Prints who owns the port and how much it matters.
    ///
    /// This lives here rather than in Program.cs because the platform analyser does not
    /// flow an OperatingSystem.IsWindows() guard into a lambda - LINQ over these
    /// Windows-only members has to sit inside a method that is itself annotated.
    /// </summary>
    public static void Report(int port)
    {
        // Exclude ourselves: on a re-run the previous instance may still be shutting
        // down, and reporting your own socket as a conflict is worse than saying nothing.
        var owners = FindListenersOnPort(port)
            .Where(o => o.Pid != Environment.ProcessId)
            .ToList();

        if (owners.Count == 0) { Log.Ok($"UDP/{port} is free."); return; }

        string self = Process.GetCurrentProcess().ProcessName;
        bool wildcard = owners.Any(o => o.LocalAddress == "0.0.0.0");

        Log.Warn($"UDP/{port} is already bound by:");
        foreach (var o in owners)
        {
            string note = o.ProcessName.Equals(self, StringComparison.OrdinalIgnoreCase)
                ? "   <-- another copy of this tool is running; close it"
                : o.LocalAddress == "0.0.0.0"
                    ? "   <-- wildcard bind, affects every adapter"
                    : "   (that address only)";
            Console.WriteLine($"      {o.ProcessName,-16} pid {o.Pid,-7} {o.LocalAddress}:{o.Port}{note}");
        }

        Console.WriteLine(wildcard
            ? """
                  A wildcard (0.0.0.0) bind competes for every broadcast request on the
                  machine. Windows permits the second bind, so delivery becomes ambiguous -
                  this is the classic "tool runs, sees nothing" case. Close it first.
              """
            : """
                  These are bound to specific addresses, so they only compete for requests
                  arriving on those subnets. Usually harmless here, but worth knowing about
                  if the adapter you care about is one of them.
              """);
        Console.WriteLine();
    }
}
