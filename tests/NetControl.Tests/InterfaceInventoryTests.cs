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
        // address, so "clear" is not the expected answer — only "did not throw" is.
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
}
