using System.Net;
using NetControl.App.ViewModels;
using NetControl.Core;
using NetControl.Core.Oui;
using NetControl.Core.Persistence;
using Xunit;

namespace NetControl.Tests;

/// <summary>
/// The plan grid, over a real project store.
///
/// <para>No window is opened and none is needed: the grid carries no WPF types, which is the point
/// of it living in a view model. What is being tested is the thing that actually decides whether
/// a device gets served - does the edit reach the file, and does anything downstream find out.</para>
/// </summary>
public sealed class DeviceGridViewModelTests : IDisposable
{
    private static readonly MacAddress Drive = MacAddress.Parse("00:1D:9C:C7:B0:70");
    private static readonly MacAddress Scanner = MacAddress.Parse("00:00:BC:5E:11:01");

    private readonly ProjectStore _project = ProjectStore.CreateInMemory("Test");
    private readonly DeviceGridViewModel _grid = new(OuiDatabase.Empty);

    public DeviceGridViewModelTests() => _grid.Load(_project);

    public void Dispose() => _project.Dispose();

    [Fact]
    public void LoadsThePlanThatIsAlreadyInTheFile()
    {
        _project.Devices.Upsert(new DeviceRecord
        {
            Mac = Drive,
            PlannedIp = IPAddress.Parse("192.168.1.51"),
            PlannedMask = IPAddress.Parse("255.255.255.0"),
        });

        _grid.Reload();

        DeviceRowViewModel row = Assert.Single(_grid.Rows);
        Assert.Equal(Drive, row.Mac);
        Assert.True(row.IsServable);
        Assert.Equal(1, _grid.ServableCount);
    }

    /// <summary>
    /// There is no Save button because there is nothing to save: the cell that was just left is
    /// already in the database. This is the test that has to hold for that claim to be true.
    /// </summary>
    [Fact]
    public void AnEditIsInTheFileAsSoonAsTheCellIsLeft()
    {
        DeviceRowViewModel row = Typed();

        DeviceRecord? stored = _project.Devices.Find(Drive);
        Assert.NotNull(stored);
        Assert.Equal(IPAddress.Parse("192.168.1.51"), stored.PlannedIp);
        Assert.Equal(row.Id, stored.Id);
    }

    /// <summary>
    /// A row is written once and then edited, not inserted again on every keystroke. Upsert is
    /// keyed on MAC so a second insert would be invisible here - it is the MAC edit below that
    /// would break, by leaving the old row in the plan and still servable.
    /// </summary>
    [Fact]
    public void EditingTheMacMovesTheRowRatherThanAddingASecondOne()
    {
        DeviceRowViewModel row = Typed();
        long id = row.Id;

        row.MacText = "00:00:BC:5E:11:01";

        Assert.Equal(1, _project.Devices.Count);
        Assert.Null(_project.Devices.Find(Drive));
        Assert.Equal(id, _project.Devices.Find(Scanner)?.Id);
    }

    [Fact]
    public void ARowThatDoesNotValidateNeverReachesTheFile()
    {
        DeviceRowViewModel row = Typed();
        row.IpText = "192.168.1.999";

        Assert.True(row.HasProblem);
        Assert.Equal(IPAddress.Parse("192.168.1.51"), _project.Devices.Find(Drive)?.PlannedIp);
    }

    /// <summary>
    /// <c>Device.Mac</c> is UNIQUE, so the alternative to catching this is a raw
    /// "SQLite Error 19" in front of someone holding a torch in a panel.
    /// </summary>
    [Fact]
    public void RefusesASecondRowWithAMacThePlanAlreadyHas()
    {
        Typed();

        DeviceRowViewModel duplicate = Add();
        duplicate.MacText = "00:1D:9C:C7:B0:70";

        Assert.True(duplicate.HasProblem);
        Assert.Contains("already in the plan", duplicate.Problem!, StringComparison.Ordinal);
        Assert.Equal(1, _project.Devices.Count);
    }

    /// <summary>
    /// Resolved at the moment the MAC is entered and stored, never looked up on read. The IEEE
    /// registry moves, and a project file has to read the same way in six months.
    /// </summary>
    [Fact]
    public void ResolvesTheVendorOnceAndStoresIt()
    {
        var grid = new DeviceGridViewModel(new StubOui("Rockwell Automation"));
        grid.Load(_project);

        grid.AddDeviceCommand.Execute(null);
        DeviceRowViewModel row = grid.Rows[0];
        row.MacText = "00:1D:9C:C7:B0:70";

        Assert.Equal("Rockwell Automation", row.Vendor);
        Assert.Equal("Rockwell Automation", _project.Devices.Find(Drive)?.Vendor);
    }

    [Fact]
    public void TellsTheOwnerWheneverThePlanHasChanged()
    {
        int changes = 0;
        _grid.PlanChanged += (_, _) => changes++;

        Typed();

        Assert.True(changes > 0);
    }

    /// <summary>An empty row added by mistake is just dropped. Nothing was ever written for it.</summary>
    [Fact]
    public void RemovesARowThatWasNeverWritten()
    {
        _grid.AddDeviceCommand.Execute(null);
        _grid.RemoveDeviceCommand.Execute(null);

        Assert.Empty(_grid.Rows);
        Assert.Equal(0, _project.Devices.Count);
    }

    [Fact]
    public void RemovingARowTakesItOutOfTheFile()
    {
        DeviceRowViewModel row = Typed();
        _grid.SelectedRow = row;

        _grid.RemoveDeviceCommand.Execute(null);

        Assert.Empty(_grid.Rows);
        Assert.Equal(0, _project.Devices.Count);
    }

    /// <summary>
    /// Once a device is in the commissioning record its row cannot be removed, and the grid has to
    /// say why rather than appearing to do nothing.
    /// </summary>
    [Fact]
    public void WillNotRemoveADeviceThatIsAlreadyInTheRecord()
    {
        DeviceRowViewModel row = Typed();
        _project.Assignments.Record(row.Id, IPAddress.Parse("192.168.1.51"), 12, "Ethernet", DateTimeOffset.UtcNow);

        _grid.SelectedRow = row;
        _grid.RemoveDeviceCommand.Execute(null);

        Assert.Single(_grid.Rows);
        Assert.Equal(1, _project.Devices.Count);
        Assert.Contains("commissioning record", _grid.ErrorMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoveIsUnavailableWithNothingSelected()
    {
        Assert.False(_grid.RemoveDeviceCommand.CanExecute(null));

        _grid.AddDeviceCommand.Execute(null);

        Assert.True(_grid.RemoveDeviceCommand.CanExecute(null));
    }

    /// <summary>
    /// Planning a device you are watching ask is the loop the whole tool is built around, so the
    /// MAC arrives from the log rather than from a keyboard - and is resolved and stored there and
    /// then.
    /// </summary>
    [Fact]
    public void PlansADeviceHandedToItFromTheLog()
    {
        DeviceRowViewModel row = _grid.PlanDevice(Drive);

        Assert.Equal(Drive, row.Mac);
        Assert.Same(row, _grid.SelectedRow);
        Assert.NotNull(_project.Devices.Find(Drive));
    }

    /// <summary>
    /// A MAC that is already planned selects the row that has it. Adding a second would throw on
    /// the UNIQUE constraint, where what the user wanted was to be shown where the device already is.
    /// </summary>
    [Fact]
    public void PlanningADeviceTwiceSelectsTheRowThatAlreadyHasIt()
    {
        DeviceRowViewModel first = _grid.PlanDevice(Drive);
        DeviceRowViewModel again = _grid.PlanDevice(Drive);

        Assert.Same(first, again);
        Assert.Single(_grid.Rows);
    }

    /// <summary>
    /// The address cell stays empty, and the tool never puts a number in it.
    ///
    /// <para>An address is a decision off a drawing - drives at .51, remote I/O at .21 - and the
    /// lowest free address on the subnet is .1, which on a real panel is the gateway. A suggestion
    /// that is wrong nearly every time is worse than none, because it is wrong in a way that is
    /// easy to accept by mistake.</para>
    /// </summary>
    [Fact]
    public void PlansTheMacAndLeavesTheAddressToTheUser()
    {
        DeviceRowViewModel row = _grid.PlanDevice(Drive);

        Assert.Equal(Drive, row.Mac);
        Assert.Equal(string.Empty, row.IpText);
        Assert.Equal(string.Empty, row.MaskText);

        // Stored, and honest about not being servable yet. Neither of those is an error state.
        Assert.NotNull(_project.Devices.Find(Drive));
        Assert.False(row.IsServable);
        Assert.Null(row.Problem);
    }

    [Fact]
    public void KnowsWhetherAMacIsAlreadyInThePlan()
    {
        Assert.False(_grid.IsPlanned(Drive));

        _grid.PlanDevice(Drive);

        Assert.True(_grid.IsPlanned(Drive));
        Assert.False(_grid.IsPlanned(Scanner));
    }

    [Fact]
    public void APlannedDeviceThatAsksIsMarkedSeen()
    {
        DeviceRowViewModel row = Typed();
        Assert.Equal(DeviceState.Planned, row.State);

        _grid.NoteSeen(Drive);

        Assert.Equal(DeviceState.Seen, row.State);
    }

    [Fact]
    public void ADeviceThatWasServedOutranksOneThatWasMerelySeen()
    {
        DeviceRowViewModel row = Typed();

        _grid.NoteServed(Drive);
        _grid.NoteSeen(Drive);

        Assert.Equal(DeviceState.Served, row.State);
    }

    /// <summary>
    /// Reopening a project has to remember what it served, because that is in the file. What it
    /// merely saw is a session fact and is not.
    /// </summary>
    [Fact]
    public void GradesARowAsServedFromTheProjectFileOnLoad()
    {
        DeviceRowViewModel row = Typed();
        _project.Assignments.Record(row.Id, IPAddress.Parse("192.168.1.51"), 12, "Ethernet", DateTimeOffset.UtcNow);

        _grid.Reload();

        Assert.Equal(DeviceState.Served, Assert.Single(_grid.Rows).State);
    }

    [Fact]
    public void LoadingAnotherProjectReplacesEverythingOnScreen()
    {
        Typed();

        using ProjectStore other = ProjectStore.CreateInMemory("Other");
        _grid.Load(other);

        Assert.Empty(_grid.Rows);
    }

    private DeviceRowViewModel Add()
    {
        _grid.AddDeviceCommand.Execute(null);
        return _grid.Rows[^1];
    }

    private DeviceRowViewModel Typed()
    {
        DeviceRowViewModel row = Add();
        row.MacText = "00:1D:9C:C7:B0:70";
        row.IpText = "192.168.1.51";
        row.MaskText = "255.255.255.0";
        return row;
    }

    /// <summary>
    /// The bundled table is refreshed from IEEE and its contents change; a test that asserted on a
    /// real vendor name would be a test that fails the day somebody runs the packer.
    /// </summary>
    private sealed class StubOui(string? vendor) : IOuiLookup
    {
        public string? Lookup(MacAddress mac) => mac.IsEmpty ? null : vendor;
    }
}
