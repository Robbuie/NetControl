using NetControl.Core.Interfaces;
using Xunit;

namespace NetControl.Tests;

/// <summary>
/// These touch the real machine, so they assert on invariants rather than on what happens to be
/// plugged in. Adapter inventory is the product's first job and it must not be the thing that
/// throws.
/// </summary>
public class InterfaceInventoryTests
{
    [Fact]
    public void EnumeratingAdaptersNeverThrowsAndNeverIncludesLoopback()
    {
        IReadOnlyList<NicInfo> nics = NicMonitor.Enumerate();

        Assert.All(nics, n => Assert.NotEqual(System.Net.NetworkInformation.NetworkInterfaceType.Loopback, n.Type));
        Assert.Equal(nics.OrderBy(n => n.Index).Select(n => n.Index), nics.Select(n => n.Index));
    }

    [Fact]
    public void TheCacheAgreesWithASingleEnumeration()
    {
        using var monitor = new NicMonitor(TimeSpan.FromMinutes(1));

        IReadOnlyList<NicInfo> snapshot = monitor.Snapshot();

        foreach (NicInfo nic in snapshot)
        {
            Assert.Equal(nic, monitor.ByIndex(nic.Index));
        }
    }

    [Fact]
    public void AnUnknownInterfaceIndexIsNullRatherThanAGuess()
    {
        using var monitor = new NicMonitor(TimeSpan.FromMinutes(1));

        Assert.Null(monitor.ByIndex(-1));
    }

    [Fact]
    public void PortInspectionIsSafeToCallAnywhere()
    {
        // On a stock Windows machine the Hyper-V Default Switch already holds UDP/67 on its own
        // address, so "clear" is not the expected answer - only "did not throw" is.
        PortConflictReport report = PortConflictDetector.Inspect(67);

        Assert.Equal(67, report.Port);
        Assert.False(string.IsNullOrWhiteSpace(report.Summary));

        if (report.Severity == PortConflictSeverity.None)
        {
            Assert.Empty(report.Owners);
        }
    }

    [Theory]
    [InlineData("255.255.255.0", 24)]
    [InlineData("255.255.0.0", 16)]
    [InlineData("255.255.255.252", 30)]
    public void ConvertsMasksToPrefixLengths(string mask, int expected)
    {
        Assert.Equal(expected, NicInfo.MaskToPrefixLength(System.Net.IPAddress.Parse(mask)));
    }

    /// <summary>
    /// Every row here is a real adapter off one engineering laptop that carried fourteen of them,
    /// of which exactly one was the Ethernet port anybody wanted. That machine is the reason the
    /// marker list exists, so it is the thing the list is tested against - and it is the honest
    /// shape of the problem, because a laptop with two adapters would never have caught the
    /// Network Bridge or the VPN miniport.
    ///
    /// This only sorts and shades the picker. It must never decide anything, which is why a false
    /// positive here costs a row in the wrong place and nothing more.
    /// </summary>
    [Theory]
    [InlineData("Ethernet", "Intel(R) Ethernet Connection (14) I219-V", false)]
    [InlineData("Wi-Fi", "Intel(R) Wi-Fi 6 AX201 160MHz", false)]
    [InlineData("vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter", true)]
    [InlineData("vEthernet (Wifi Switch)", "Hyper-V Virtual Ethernet Adapter #2", true)]
    [InlineData("VMware Network Adapter VMnet1", "VMware Virtual Ethernet Adapter for VMnet1", true)]
    [InlineData("Ethernet 3", "VirtualBox Host-Only Ethernet Adapter #2", true)]
    [InlineData("Local Area Connection", "TAP-Windows Adapter V9", true)]
    [InlineData("Bluetooth Network Connection", "Bluetooth Device (Personal Area Network)", true)]
    [InlineData("Network Bridge", "Microsoft Network Adapter Multiplexor Driver", true)]
    [InlineData("Ethernet 4", "Cisco AnyConnect Virtual Miniport Adapter for Windows x64", true)]
    [InlineData("OpenVPN Wintun", "Wintun Userspace Tunnel", true)]
    public void RanksRealPortsAboveTheDozenVirtualOnes(string name, string description, bool expected)
    {
        NicInfo nic = Nics.Healthy() with { Name = name, Description = description };

        Assert.Equal(expected, nic.IsLikelyVirtual);
    }
}
