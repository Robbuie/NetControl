using System.Net;
using NetControl.App.Diagnostics;
using NetControl.App.Serving;
using NetControl.App.ViewModels;
using NetControl.Core;
using NetControl.Core.Dhcp;
using NetControl.Core.Oui;
using NetControl.Core.Persistence;
using Xunit;

namespace NetControl.Tests;

/// <summary>
/// Mostly about the one rule that matters most: serve mode is explicit, and no arrangement of
/// clicks short of a deliberate one can put addresses on a plant network.
///
/// Nothing here opens a socket. The listener is only ever started through
/// <see cref="ServerController"/>, and these tests never start it.
/// </summary>
public sealed class MainViewModelTests : IDisposable
{
    private static readonly MacAddress Drive = MacAddress.Parse("00:1D:9C:C7:B0:70");

    private readonly ProjectStore _project = ProjectStore.CreateInMemory("Test");
    private readonly StaticMapPolicy _policy = new();
    private readonly FakeNicInventory _nics = new();
    private readonly ServerController _controller;
    private readonly MainViewModel _viewModel;

    public MainViewModelTests()
    {
        _controller = new ServerController(_nics, _policy);
        _viewModel = new MainViewModel(
            new ImmediateDispatcher(),
            _nics,
            new StubPreflight(),
            _controller,
            _policy,
            OuiDatabase.Empty,
            _project,
            new TestTimeProvider(Events.At));
    }

    [Fact]
    public void StartsInWatchWithNothingArmed()
    {
        Assert.False(_viewModel.IsServeArmed);
        Assert.False(_viewModel.IsRunning);
        Assert.True(_viewModel.StartWatchCommand.CanExecute(null));
    }

    /// <summary>
    /// Two deliberate actions before an address can leave the laptop: choose the adapter, then
    /// arm serving. A rogue DHCP server on a plant network is a genuinely serious incident, so
    /// this must never be reachable by accident.
    /// </summary>
    [Fact]
    public void ServeNeedsAnAdapterAndAnExplicitArming()
    {
        Assert.False(_viewModel.StartServeCommand.CanExecute(null));

        _viewModel.IsServeArmed = true;
        Assert.False(_viewModel.StartServeCommand.CanExecute(null));

        _viewModel.InterfaceBar.ApplyInventory([Nics.Healthy()]);
        _viewModel.InterfaceBar.SelectedAdapter = _viewModel.InterfaceBar.Adapters[0];

        Assert.True(_viewModel.StartServeCommand.CanExecute(null));
    }

    [Fact]
    public void ServeStaysUnavailableOnAnAdapterThatCannotSourceAReply()
    {
        _viewModel.InterfaceBar.ApplyInventory([Nics.Apipa()]);
        _viewModel.InterfaceBar.SelectedAdapter = _viewModel.InterfaceBar.Adapters[0];
        _viewModel.IsServeArmed = true;

        Assert.False(_viewModel.StartServeCommand.CanExecute(null));
    }

    [Fact]
    public void ChoosingAnAdapterTellsTheLogWhichOneToExpect()
    {
        _viewModel.InterfaceBar.ApplyInventory([Nics.Healthy(index: 12)]);
        _viewModel.InterfaceBar.SelectedAdapter = _viewModel.InterfaceBar.Adapters[0];

        Assert.Equal(12, _viewModel.Log.SelectedInterfaceIndex);
    }

    [Fact]
    public void LoadsThePlanFromTheProjectIntoThePolicy()
    {
        _project.Devices.Upsert(new DeviceRecord
        {
            Mac = Drive,
            PlannedIp = IPAddress.Parse("192.168.1.51"),
            PlannedMask = IPAddress.Parse("255.255.255.0"),
        });

        _viewModel.ReloadPlan();

        Assert.Equal(1, _viewModel.PlannedDeviceCount);
        Assert.True(_policy.TryGet(Drive, out _));
    }

    /// <summary>
    /// "3 of 14 planned devices will not be served" is something to find out at the desk, not by
    /// watching a panel stay dark, so a row that cannot become a valid assignment gets a line in
    /// the log rather than being skipped in silence.
    /// </summary>
    [Fact]
    public void ReportsAPlanRowThatCannotBeServed()
    {
        _project.Devices.Upsert(new DeviceRecord
        {
            Mac = Drive,
            PlannedIp = IPAddress.Parse("192.168.1.51"),

            // Set bits are not contiguous, so this can never become a DeviceAssignment.
            PlannedMask = IPAddress.Parse("255.255.0.255"),
        });

        _viewModel.ReloadPlan();

        Assert.Equal(0, _viewModel.PlannedDeviceCount);
        Assert.Contains(
            _viewModel.Log.Entries,
            e => e.Message.Contains("subnet mask", StringComparison.Ordinal));
    }

    [Fact]
    public void OpeningAProjectSwitchesToItsPlan()
    {
        using var temp = new TempProject();

        using (ProjectStore seeded = temp.Open("Panel 3"))
        {
            seeded.Devices.Upsert(new DeviceRecord
            {
                Mac = Drive,
                PlannedIp = IPAddress.Parse("192.168.1.51"),
                PlannedMask = IPAddress.Parse("255.255.255.0"),
            });
        }

        _viewModel.OpenProject(temp.FilePath);

        Assert.Null(_viewModel.ErrorMessage);
        Assert.Equal("Panel 3", _viewModel.ProjectName);
        Assert.Equal(1, _viewModel.PlannedDeviceCount);
        Assert.True(_policy.TryGet(Drive, out _));
    }

    /// <summary>
    /// The gesture the whole tool is built around: you watch a device ask, you plan it, and it is
    /// served on its next retransmit. That only works if a grid edit reaches the dictionary the
    /// receive loop reads, in the same gesture and with nothing else to press.
    /// </summary>
    [Fact]
    public void AGridEditReachesThePolicyTheReceiveLoopReads()
    {
        _viewModel.Plan.AddDeviceCommand.Execute(null);
        DeviceRowViewModel row = _viewModel.Plan.Rows[0];

        row.MacText = "00:1D:9C:C7:B0:70";
        row.IpText = "192.168.1.51";
        row.MaskText = "255.255.255.0";

        Assert.True(_policy.TryGet(Drive, out _));
        Assert.Equal(1, _viewModel.PlannedDeviceCount);
    }

    /// <summary>
    /// Every cell the user leaves rebuilds the plan, and a row halfway through being typed is
    /// "unservable" for as long as it takes to type the rest of it. A log line for each of those
    /// would bury the requests the log exists to show - the row says it instead, which is where
    /// the fix is anyway.
    /// </summary>
    [Fact]
    public void TypingIntoTheGridDoesNotFillTheLogWithPlanComplaints()
    {
        _viewModel.Plan.AddDeviceCommand.Execute(null);
        DeviceRowViewModel row = _viewModel.Plan.Rows[0];

        row.MacText = "00:1D:9C:C7:B0:70";
        row.IpText = "192.168.1.51";

        Assert.DoesNotContain(
            _viewModel.Log.Entries,
            e => e.Message.StartsWith("Plan:", StringComparison.Ordinal));

        // The row is still saying so, which is the point.
        Assert.False(row.IsServable);
    }

    /// <summary>
    /// The whole loop, end to end and in one gesture: a device asks, the log shows it, and
    /// double-clicking that row leaves it planned, addressed, in the file, and in the dictionary
    /// the receive loop reads. Everything before this made you type the MAC you were looking at.
    /// </summary>
    [Fact]
    public void PlanningFromALogRowReachesTheFileAndThePolicy()
    {
        _viewModel.Log.OnRequest(Events.Request(Drive, AssignmentDecision.Ignore($"{Drive} is not in the plan")));
        _viewModel.PlanFromLog(_viewModel.Log.Entries[0]);

        DeviceRowViewModel row = Assert.Single(_viewModel.Plan.Rows);
        Assert.Equal(Drive, row.Mac);
        Assert.NotNull(_project.Devices.Find(Drive));

        // The address is the user's to type, so the row is not servable yet and the policy the
        // receive loop reads must not be carrying it.
        Assert.Equal(string.Empty, row.IpText);
        Assert.False(_policy.TryGet(Drive, out _));

        row.IpText = "192.168.1.51";
        row.MaskText = "255.255.255.0";

        Assert.True(_policy.TryGet(Drive, out _));
    }

    /// <summary>
    /// Nothing about adding a row to the plan puts a packet on the network. Serve mode is still
    /// two deliberate actions away, and this is the row of the log that says so.
    /// </summary>
    [Fact]
    public void PlanningFromALogRowArmsNothing()
    {
        _viewModel.Log.OnRequest(Events.Request(Drive, AssignmentDecision.Ignore("not in the plan")));
        _viewModel.PlanFromLog(_viewModel.Log.Entries[0]);

        Assert.False(_viewModel.IsServeArmed);
        Assert.False(_viewModel.IsRunning);
    }

    /// <summary>
    /// The log carries the tool's own notices as well as devices, and double-clicking one has to
    /// say why nothing happened rather than appearing to be broken.
    /// </summary>
    [Fact]
    public void ALogRowWithNoMacSaysWhyItCannotBePlanned()
    {
        _viewModel.Log.AddNotice("Watching. Nothing will be transmitted.");

        _viewModel.PlanFromLog(_viewModel.Log.Entries[0]);

        Assert.Empty(_viewModel.Plan.Rows);
        Assert.Contains(
            _viewModel.Log.Entries,
            e => e.Message.Contains("no MAC to add to the plan", StringComparison.Ordinal));
    }

    /// <summary>
    /// A project can only ever be missing a location, never its contents - SQLite has already
    /// written everything. Say so on screen, because everyone arrives looking for a Save button.
    /// </summary>
    [Fact]
    public void StartsWithoutAFileAndSaysSo()
    {
        Assert.True(_viewModel.IsUnsavedLocation);
        Assert.Null(_viewModel.ProjectPath);
    }

    [Fact]
    public void SavingAsCarriesTheWorkOverAndKeepsWritingThere()
    {
        using var temp = new TempProject("panel-3");

        _viewModel.Plan.PlanDevice(Drive);
        _viewModel.SaveProjectAs(temp.FilePath);

        Assert.Null(_viewModel.ErrorMessage);
        Assert.False(_viewModel.IsUnsavedLocation);
        Assert.Equal(temp.FilePath, _viewModel.ProjectPath);
        Assert.Equal("panel-3", _viewModel.ProjectName);

        // The grid is pointed at the new file, not the in-memory store it came from.
        Assert.Single(_viewModel.Plan.Rows);

        using ProjectStore reopened = ProjectStore.Open(temp.FilePath);
        Assert.NotNull(reopened.Devices.Find(Drive));
    }

    [Fact]
    public void ANewProjectStartsEmptyAndUnsaved()
    {
        _viewModel.Plan.PlanDevice(Drive);

        _viewModel.NewProject();

        Assert.True(_viewModel.IsUnsavedLocation);
        Assert.Empty(_viewModel.Plan.Rows);
        Assert.Equal(0, _viewModel.PlannedDeviceCount);
        Assert.False(_policy.TryGet(Drive, out _));
    }

    public void Dispose()
    {
        // MainViewModel owns the controller and the project it was handed.
        _viewModel.Dispose();
    }

}
