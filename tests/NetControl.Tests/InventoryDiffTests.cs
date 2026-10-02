using System.Net;
using NetControl.Core;
using NetControl.Core.Discovery;
using NetControl.Core.Persistence;
using Xunit;

namespace NetControl.Tests;

/// <summary>
/// What changed between two scans. Pure, so every case is arranged by hand.
///
/// <para>The rule underneath all of them: <b>identity, not address, decides what is the same
/// device</b> - so a re-addressed drive is reported as one device that moved, and an empty slot is
/// reported as silence rather than as a removal.</para>
/// </summary>
public class InventoryDiffTests
{
    private static readonly DateTimeOffset Earlier = new(2026, 9, 28, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheSameDevicesAtTheSameAddressesIsNoChange()
    {
        InventoryDiffResult result = InventoryDiff.Compare(
            [Seen("192.168.1.51", 1), Seen("192.168.1.52", 2)],
            [Seen("192.168.1.52", 2), Seen("192.168.1.51", 1)],
            Earlier);

        Assert.False(result.HasChanges);
        Assert.Equal(2, result.Unchanged);
        Assert.Contains("No change", result.Summary, StringComparison.Ordinal);
        Assert.Contains("2026-09-28 14:00 UTC", result.Summary, StringComparison.Ordinal);
    }

    /// <summary>The change worth the most on a call-out: something re-addressed a device.</summary>
    [Fact]
    public void ADeviceAtANewAddressHasMovedRatherThanVanishedAndAppeared()
    {
        InventoryDiffResult result = InventoryDiff.Compare(
            [Seen("192.168.1.51", 1)],
            [Seen("192.168.1.60", 1)],
            Earlier);

        InventoryChange change = Assert.Single(result.Changes);
        Assert.Equal(InventoryChangeKind.Moved, change.Kind);
        Assert.Equal(EventSeverity.Warn, change.Severity);
        Assert.Contains("from 192.168.1.51 to 192.168.1.60", change.Message, StringComparison.Ordinal);
    }

    /// <summary>Same product, same address, new serial: a module was swapped, and the old one is not "missing".</summary>
    [Fact]
    public void ANewSerialOfTheSameProductAtTheSameAddressIsAReplacement()
    {
        InventoryDiffResult result = InventoryDiff.Compare(
            [Seen("192.168.1.51", 1)],
            [Seen("192.168.1.51", 7)],
            Earlier);

        InventoryChange change = Assert.Single(result.Changes);
        Assert.Equal(InventoryChangeKind.Replaced, change.Kind);
        Assert.Equal(EventSeverity.Info, change.Severity);
        Assert.Contains("00000001", change.Message, StringComparison.Ordinal);
        Assert.Contains("00000007", change.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADifferentProductAtAnAddressIsAWarn()
    {
        InventoryDiffResult result = InventoryDiff.Compare(
            [Seen("192.168.1.51", 1)],
            [Seen("192.168.1.51", 9, product: 999, name: "PanelView 800")],
            Earlier);

        InventoryChange change = Assert.Single(result.Changes);
        Assert.Equal(InventoryChangeKind.DifferentDevice, change.Kind);
        Assert.Equal(EventSeverity.Warn, change.Severity);
        Assert.Contains("PanelView 800", change.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportsChangedFirmware()
    {
        InventoryDiffResult result = InventoryDiff.Compare(
            [Seen("192.168.1.51", 1, revision: "11.03")],
            [Seen("192.168.1.51", 1, revision: "12.01")],
            Earlier);

        InventoryChange change = Assert.Single(result.Changes);
        Assert.Equal(InventoryChangeKind.FirmwareChanged, change.Kind);
        Assert.Contains("12.01 (was 11.03)", change.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Silence is not absence. A device that answered last time and not now may be off for the shift:
    /// Info, and worded as not answering.
    /// </summary>
    [Fact]
    public void ADeviceThatWentSilentIsNotAnsweringAndOnlyInfo()
    {
        InventoryDiffResult result = InventoryDiff.Compare(
            [Seen("192.168.1.51", 1), Seen("192.168.1.52", 2)],
            [Seen("192.168.1.51", 1)],
            Earlier);

        InventoryChange change = Assert.Single(result.Changes);
        Assert.Equal(InventoryChangeKind.NotAnswering, change.Kind);
        Assert.Equal(EventSeverity.Info, change.Severity);
        Assert.Contains("did not answer", change.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANewDeviceIsInfo()
    {
        InventoryDiffResult result = InventoryDiff.Compare(
            [Seen("192.168.1.51", 1)],
            [Seen("192.168.1.51", 1), Seen("192.168.1.70", 3)],
            Earlier);

        InventoryChange change = Assert.Single(result.Changes);
        Assert.Equal(InventoryChangeKind.New, change.Kind);
        Assert.Equal(1, result.Unchanged);
    }

    /// <summary>
    /// A device that moved away from an address, with something new arriving there, is two facts -
    /// it moved, and the newcomer is new - and not a "different device at this address": the old
    /// one is still answering, just somewhere else.
    /// </summary>
    [Fact]
    public void ANewcomerAtAnAddressSomethingMovedAwayFromIsNewNotAReplacement()
    {
        InventoryDiffResult result = InventoryDiff.Compare(
            [Seen("192.168.1.51", 1)],
            [Seen("192.168.1.60", 1), Seen("192.168.1.51", 5, product: 999)],
            Earlier);

        Assert.Equal(2, result.Changes.Count);
        Assert.Contains(result.Changes, c => c.Kind == InventoryChangeKind.Moved);
        Assert.Contains(result.Changes, c => c.Kind == InventoryChangeKind.New);
        Assert.DoesNotContain(result.Changes, c => c.Kind == InventoryChangeKind.DifferentDevice);
    }

    /// <summary>Warnings first, then by address as a number - .2 before .100, as on the panel.</summary>
    [Fact]
    public void SortsWorstFirstThenByAddressAsANumber()
    {
        InventoryDiffResult result = InventoryDiff.Compare(
            [Seen("192.168.1.100", 1), Seen("192.168.1.2", 2)],
            [Seen("192.168.1.100", 1), Seen("192.168.1.150", 3), Seen("192.168.1.9", 4), Seen("192.168.1.200", 2)],
            Earlier);

        // .2's device moved to .200 (Warn); .9 and .150 are new (Info).
        Assert.Equal(InventoryChangeKind.Moved, result.Changes[0].Kind);
        Assert.Equal(IPAddress.Parse("192.168.1.9"), result.Changes[1].Address);
        Assert.Equal(IPAddress.Parse("192.168.1.150"), result.Changes[2].Address);
    }

    /// <summary>A device reporting serial 0 is still told apart from its neighbours, by its MAC.</summary>
    [Fact]
    public void FallsBackToTheMacForADeviceWithNoSerial()
    {
        ScanSightingRecord before = Seen("192.168.1.51", 0) with { Mac = MacAddress.Parse("00:1D:9C:C7:B0:70") };
        ScanSightingRecord after = before with { Address = IPAddress.Parse("192.168.1.61") };

        InventoryDiffResult result = InventoryDiff.Compare([before], [after], Earlier);

        Assert.Equal(InventoryChangeKind.Moved, Assert.Single(result.Changes).Kind);
    }

    private static ScanSightingRecord Seen(
        string address, uint serial, ushort product = 167, string name = "1734-AENT/B", string revision = "11.03") =>
        new()
        {
            Address = IPAddress.Parse(address),
            VendorId = 1,
            DeviceType = 12,
            ProductCode = product,
            Serial = serial,
            ProductName = name,
            Revision = revision,
        };
}
