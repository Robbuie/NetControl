using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NetControl.Core.Interfaces;

/// <summary>
/// Finds out who already owns a UDP port, by process.
///
/// This is the single most common reason a BOOTP tool appears to start fine and then never
/// receives anything: VMware, VirtualBox, WSL/Hyper-V, Docker or a stale copy of the tool is
/// holding UDP/67. Windows permits the second bind when SO_REUSEADDR is in play, so the symptom
/// is silence rather than an error - which is precisely why this has to be checked up front
/// instead of waited for.
///
/// A stock Windows machine already has one: the Hyper-V Default Switch, bound to its own address.
/// That is why severity is graded rather than boolean.
/// </summary>
public static class PortConflictDetector
{
    /// <summary>
    /// Inspects a port and grades what it finds. Safe to call on any platform; on a non-Windows
    /// host it returns <see cref="PortConflictReport.Unknown"/> rather than pretending the port
    /// is clear, because "we did not check" and "nothing is there" are different answers.
    /// </summary>
    public static PortConflictReport Inspect(int port)
    {
        if (!OperatingSystem.IsWindows())
        {
            return PortConflictReport.Unknown(port);
        }

        return InspectWindows(port);
    }

    /// <summary>
    /// Raw list of processes holding the port, including this one. Callers that want the graded
    /// verdict should use <see cref="Inspect"/>.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<UdpPortOwner> FindOwners(int port)
    {
        var results = new List<UdpPortOwner>();

        int size = 0;
        uint rc = NativeMethods.GetExtendedUdpTable(
            IntPtr.Zero, ref size, false, NativeMethods.AF_INET, NativeMethods.UDP_TABLE_OWNER_PID, 0);

        // A zero-length table is legal and comes back as NO_ERROR with size 0.
        if (rc != NativeMethods.ERROR_INSUFFICIENT_BUFFER && rc != NativeMethods.NO_ERROR)
        {
            return results;
        }

        if (size == 0)
        {
            return results;
        }

        IntPtr table = Marshal.AllocHGlobal(size);
        try
        {
            rc = NativeMethods.GetExtendedUdpTable(
                table, ref size, false, NativeMethods.AF_INET, NativeMethods.UDP_TABLE_OWNER_PID, 0);
            if (rc != NativeMethods.NO_ERROR)
            {
                return results;
            }

            int count = Marshal.ReadInt32(table);
            IntPtr row = table + sizeof(int);

            for (int i = 0; i < count; i++, row += NativeMethods.UdpRowOwnerPidSize)
            {
                uint localAddr = (uint)Marshal.ReadInt32(row);
                uint localPortRaw = (uint)Marshal.ReadInt32(row + 4);
                int pid = Marshal.ReadInt32(row + 8);

                // dwLocalPort carries the port in NETWORK byte order in its low two bytes, even
                // though the field itself is a host-order DWORD. Read it deliberately.
                int localPort = BinaryPrimitives.ReadUInt16BigEndian(BitConverter.GetBytes(localPortRaw));
                if (localPort != port)
                {
                    continue;
                }

                // dwLocalAddr is already in network order, which is the order IPAddress wants.
                var address = new IPAddress(BitConverter.GetBytes(localAddr));
                results.Add(new UdpPortOwner(address, localPort, pid, DescribeProcess(pid)));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(table);
        }

        return results;
    }

    [SupportedOSPlatform("windows")]
    private static PortConflictReport InspectWindows(int port)
    {
        // Exclude ourselves. On a restart the previous socket may still be draining, and
        // reporting your own bind as a conflict is worse than saying nothing at all.
        int self = Environment.ProcessId;
        string selfName = Process.GetCurrentProcess().ProcessName;

        List<UdpPortOwner> owners = FindOwners(port).Where(o => o.ProcessId != self).ToList();

        if (owners.Count == 0)
        {
            return new PortConflictReport(
                port,
                PortConflictSeverity.None,
                owners,
                $"UDP/{port} is free.",
                null);
        }

        UdpPortOwner? sibling = owners.FirstOrDefault(
            o => o.ProcessName.Equals(selfName, StringComparison.OrdinalIgnoreCase));
        List<UdpPortOwner> wildcards = owners.Where(o => o.IsWildcard).ToList();

        if (sibling is not null)
        {
            return new PortConflictReport(
                port,
                PortConflictSeverity.Serious,
                owners,
                $"Another copy of this tool ({sibling.ProcessName}, pid {sibling.ProcessId}) already holds "
                    + $"UDP/{port}. Requests will be delivered to whichever instance Windows picks.",
                $"Close the other instance (pid {sibling.ProcessId}) and start again.");
        }

        if (wildcards.Count > 0)
        {
            string names = Describe(wildcards);
            return new PortConflictReport(
                port,
                PortConflictSeverity.Serious,
                owners,
                $"UDP/{port} is already bound on 0.0.0.0 by {names}. A wildcard bind competes for "
                    + "broadcast requests on every adapter, and Windows delivers to only one socket - "
                    + "this is the usual cause of a BOOTP tool that runs but never sees a device.",
                $"Stop {names} before starting the server, or accept that requests may go to it instead.");
        }

        string others = Describe(owners);
        return new PortConflictReport(
            port,
            PortConflictSeverity.Advisory,
            owners,
            $"UDP/{port} is bound by {others}, but to specific addresses rather than 0.0.0.0, so it only "
                + "competes for requests arriving on those subnets.",
            "Usually harmless. Worth checking only if the adapter you are serving on is one of those.");
    }

    private static string Describe(IReadOnlyList<UdpPortOwner> owners) =>
        string.Join(", ", owners.Select(o =>
            string.Create(CultureInfo.InvariantCulture, $"{o.ProcessName} (pid {o.ProcessId}) on {o.LocalAddress}")));

    private static string DescribeProcess(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            return "(process has exited)";
        }
        catch (InvalidOperationException)
        {
            return "(process has exited)";
        }
    }
}
