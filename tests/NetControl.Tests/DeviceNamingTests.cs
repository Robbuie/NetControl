using NetControl.App.Serving;
using NetControl.Core;
using NetControl.Core.Persistence;
using Xunit;

namespace NetControl.Tests;

/// <summary>
/// What a device is called, and the one place that decides it.
///
/// <para>The rule lives on <see cref="DeviceRecord"/> rather than in a view model so that the
/// grid, the live log and anything that reports on a session afterwards cannot disagree. These
/// tests are the statement of that rule.</para>
/// </summary>
public sealed class DeviceNamingTests
{
    private static readonly MacAddress Mac = MacAddress.Parse("00:1D:9C:C7:B0:70");
    private static readonly MacAddress Other = MacAddress.Parse("00:0F:73:11:22:33");

    [Fact]
    public void RoleIsWhatADeviceIsCalled()
    {
        DeviceRecord device = Device(role: "PowerFlex 525 conveyor 3", hostName: "drive-3", panelRef: "PNL-7");

        Assert.Equal("PowerFlex 525 conveyor 3", device.DisplayName);
    }

    [Fact]
    public void HostNameIsUsedWhenThereIsNoRole()
    {
        Assert.Equal("drive-3", Device(hostName: "drive-3", panelRef: "PNL-7").DisplayName);
    }

    [Fact]
    public void PanelRefIsTheLastResortRatherThanNothing()
    {
        Assert.Equal("PNL-7", Device(panelRef: "PNL-7").DisplayName);
    }

    /// <summary>
    /// A cell someone tabbed through and left holding spaces is an empty cell. Treating it as a
    /// name would put a blank-looking device column beside a MAC and leave no way to tell that
    /// from a device nobody had labelled.
    /// </summary>
    [Fact]
    public void WhitespaceIsNotAName()
    {
        DeviceRecord device = Device(role: "   ", hostName: "\t", panelRef: "PNL-7");

        Assert.Equal("PNL-7", device.DisplayName);
    }

    [Fact]
    public void ANameIsTrimmed()
    {
        Assert.Equal("drive-3", Device(role: "  drive-3  ").DisplayName);
    }

    /// <summary>
    /// Null rather than the MAC. A device nobody has labelled has no name, and saying so lets each
    /// caller decide what to show - the log leaves the cell blank beside a MAC column that already
    /// carries the MAC, and repeating it there would be noise.
    /// </summary>
    [Fact]
    public void AnUnlabelledDeviceHasNoName()
    {
        Assert.Null(Device().DisplayName);
    }

    /// <summary>
    /// The index covers the plan, not the servable subset of it. A row with a MAC and no address
    /// is a normal row - it is what "Add to plan" from a log row produces - and it has to come back
    /// as planned or the log calls the device a stranger.
    /// </summary>
    [Fact]
    public void ThePlanIndexHoldsARowThatCouldNeverBeServed()
    {
        var index = new PlanIndex();

        index.Reload([Device(role: "Conveyor 3 drive")]);

        PlannedDevice? found = index.Find(Mac);

        Assert.NotNull(found);
        Assert.Equal("Conveyor 3 drive", found.DisplayName);
    }

    [Fact]
    public void ThePlanIndexDoesNotInventARowForAMacItWasNotGiven()
    {
        var index = new PlanIndex();

        index.Reload([Device(role: "Conveyor 3 drive")]);

        Assert.Null(index.Find(Other));
    }

    /// <summary>
    /// Reload replaces rather than merges. A device deleted from the plan must disappear from the
    /// index too, or the log would keep naming a device the file no longer holds.
    /// </summary>
    [Fact]
    public void ReloadReplacesThePlanRatherThanAddingToIt()
    {
        var index = new PlanIndex();

        index.Reload([Device(role: "Conveyor 3 drive"), Device(Other, role: "Conveyor 4 drive")]);
        Assert.Equal(2, index.Count);

        index.Reload([Device(Other, role: "Conveyor 4 drive")]);

        Assert.Equal(1, index.Count);
        Assert.Null(index.Find(Mac));
        Assert.Equal("Conveyor 4 drive", index.Find(Other)?.DisplayName);
    }

    [Fact]
    public void ClearingThePlanIndexEmptiesIt()
    {
        var index = new PlanIndex();

        index.Reload([Device(role: "Conveyor 3 drive")]);
        index.Clear();

        Assert.Equal(0, index.Count);
        Assert.Null(index.Find(Mac));
    }

    private static DeviceRecord Device(
        MacAddress? mac = null,
        string? role = null,
        string? hostName = null,
        string? panelRef = null) =>
        new()
        {
            Mac = mac ?? Mac,
            Role = role,
            HostName = hostName,
            PanelRef = panelRef,
        };
}
