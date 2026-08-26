using System.Net;
using System.Text;
using NetControl.App.Serving;
using NetControl.App.ViewModels;
using NetControl.Core;
using NetControl.Core.Dhcp;
using NetControl.Core.Oui;
using NetControl.Core.Persistence;
using Xunit;

namespace NetControl.Tests;

/// <summary>
/// CSV import and export as the window uses them: what reaches the plan, what reaches the policy
/// the receive loop reads, and what reaches the append-only record.
///
/// <para>Nothing here opens a socket. The rule under test is not how a file is parsed - that is
/// <see cref="PlanCsvTests"/> and <see cref="PlanValidationTests"/> - but that a refused file
/// leaves the project exactly as it was, and that either outcome is accounted for afterwards.</para>
/// </summary>
public sealed class MainViewModelPlanFileTests : IDisposable
{
    private const string Header = "Mac,Ip,Mask,Gateway,HostName,PanelRef,Role,Notes\r\n";

    private static readonly MacAddress Drive = MacAddress.Parse("00:1D:9C:C7:B0:70");

    private readonly ProjectStore _project = ProjectStore.CreateInMemory("Test");
    private readonly StaticMapPolicy _policy = new();
    private readonly FakeNicInventory _nics = new();
    private readonly ServerController _controller;
    private readonly MainViewModel _viewModel;
    private readonly string _directory;

    public MainViewModelPlanFileTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "netcontrol-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);

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

    /// <summary>
    /// The whole point: a file becomes rows in the grid <em>and</em> an entry in the dictionary the
    /// receive loop reads. A plan that reached the grid but not the policy is a plan that looks
    /// imported and serves nothing.
    /// </summary>
    [Fact]
    public void ImportsAFileIntoThePlanAndIntoThePolicy()
    {
        _viewModel.ImportPlan(WriteCsv(Header
            + "00:1D:9C:C7:B0:70,192.168.1.51,255.255.255.0,,,MCC-3,Conveyor 3 drive,\r\n"
            + "00:1D:9C:C7:B0:71,192.168.1.52,255.255.255.0,,,MCC-3,Conveyor 4 drive,\r\n"));

        Assert.Null(_viewModel.ErrorMessage);
        Assert.Equal(2, _viewModel.Plan.Rows.Count);
        Assert.Equal(2, _viewModel.PlannedDeviceCount);
        Assert.True(_policy.TryGet(Drive, out _));
    }

    /// <summary>A plan file was read and acted on. That belongs in the commissioning record.</summary>
    [Fact]
    public void RecordsTheImportInTheAppendOnlyLog()
    {
        string path = WriteCsv(Header + "00:1D:9C:C7:B0:70,192.168.1.51,255.255.255.0,,,,,\r\n");

        _viewModel.ImportPlan(path);

        EventRecord row = Assert.Single(_project.Events.All(), e => e.Category == EventCategory.App);
        Assert.Equal(EventSeverity.Info, row.Severity);
        Assert.Contains("1 device imported", row.Message, StringComparison.Ordinal);
        Assert.Equal(path, row.Target);
    }

    /// <summary>
    /// The rule the whole feature is built around. One bad row and the plan is untouched - not
    /// mostly untouched, not the good rows only. Half a panel planned is worse than none because it
    /// looks finished.
    /// </summary>
    [Fact]
    public void ARefusedFileLeavesThePlanExactlyAsItWas()
    {
        _project.Devices.Upsert(new DeviceRecord
        {
            Mac = Drive,
            PlannedIp = IPAddress.Parse("192.168.1.51"),
            PlannedMask = IPAddress.Parse("255.255.255.0"),
            Role = "Typed in by hand",
        });
        _viewModel.Plan.Reload();
        _viewModel.ReloadPlan(announceProblems: false);

        _viewModel.ImportPlan(WriteCsv(Header
            + "00:1D:9C:C7:B0:80,192.168.1.60,255.255.255.0,,,,,\r\n"
            + "00:1D:9C:C7:B0:81,192.168.1.999,255.255.255.0,,,,,\r\n"));

        // Not the good row from the file, and not a merge of the two: the row that was there.
        DeviceRowViewModel row = Assert.Single(_viewModel.Plan.Rows);
        Assert.Equal("Typed in by hand", row.Role);
        Assert.False(_policy.TryGet(MacAddress.Parse("00:1D:9C:C7:B0:80"), out _));
    }

    /// <summary>Every problem, with its line, where the person can read them all at once.</summary>
    [Fact]
    public void ListsEveryProblemAndSaysThePlanIsUnchanged()
    {
        _viewModel.ImportPlan(WriteCsv(Header
            + "not-a-mac,192.168.1.51,255.255.255.0,,,,,\r\n"
            + "00:1D:9C:C7:B0:71,192.168.1.999,255.255.255.0,,,,,\r\n"));

        Assert.Contains("2 problems", _viewModel.ErrorMessage ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("has not been changed", _viewModel.ErrorRemediation ?? string.Empty, StringComparison.Ordinal);

        Assert.Contains(
            _viewModel.Log.Entries,
            entry => entry.Message.Contains("line 2", StringComparison.Ordinal));
        Assert.Contains(
            _viewModel.Log.Entries,
            entry => entry.Message.Contains("line 3", StringComparison.Ordinal));
    }

    /// <summary>A refusal is a thing that happened to a project, so it is recorded like one.</summary>
    [Fact]
    public void RecordsARefusalAsAWarning()
    {
        _viewModel.ImportPlan(WriteCsv(Header + "not-a-mac,192.168.1.51,255.255.255.0,,,,,\r\n"));

        EventRecord row = Assert.Single(_project.Events.All(), e => e.Category == EventCategory.App);
        Assert.Equal(EventSeverity.Warn, row.Severity);
        Assert.Contains("refused", row.Message, StringComparison.Ordinal);
    }

    /// <summary>A file that is not a plan at all says so once, rather than once per row.</summary>
    [Fact]
    public void RefusesAFileThatIsNotAPlanWithoutListingRows()
    {
        _viewModel.ImportPlan(WriteCsv("Ip,Mask\r\n192.168.1.51,255.255.255.0\r\n"));

        Assert.Contains("no MAC column", _viewModel.ErrorMessage ?? string.Empty, StringComparison.Ordinal);
        Assert.Empty(_viewModel.Plan.Rows);
    }

    /// <summary>
    /// Export and import are the same format, so a plan can go out to a spreadsheet and come back.
    /// This is the test that fails if either side is changed without the other.
    /// </summary>
    [Fact]
    public void ExportsAPlanThatImportsBackUnchanged()
    {
        _project.Devices.Upsert(new DeviceRecord
        {
            Mac = Drive,
            PlannedIp = IPAddress.Parse("192.168.1.51"),
            PlannedMask = IPAddress.Parse("255.255.255.0"),
            PlannedGateway = IPAddress.Parse("192.168.1.1"),
            HostName = "aent-01",
            PanelRef = "MCC-3",
            Role = "Conveyor 3 drive",
            Notes = "Replaced drive, panel 3. Said \"F5 comms loss\".",
        });
        _viewModel.Plan.Reload();

        string path = Path.Combine(_directory, "plan.csv");
        _viewModel.ExportPlan(path);

        _viewModel.NewProject();
        Assert.Empty(_viewModel.Plan.Rows);

        _viewModel.ImportPlan(path);

        DeviceRowViewModel row = Assert.Single(_viewModel.Plan.Rows);
        Assert.Equal("00:1D:9C:C7:B0:70", row.MacText);
        Assert.Equal("192.168.1.51", row.IpText);
        Assert.Equal("192.168.1.1", row.GatewayText);
        Assert.Equal("Conveyor 3 drive", row.Role);
        Assert.Equal("Replaced drive, panel 3. Said \"F5 comms loss\".", row.Notes);
    }

    /// <summary>
    /// Re-importing a corrected file corrects the plan rather than doubling it. Rows are matched by
    /// MAC, which is the only thing about a device that does not change when somebody fixes a typo.
    /// </summary>
    [Fact]
    public void ReimportingACorrectedFileReplacesTheRowRatherThanAddingASecond()
    {
        _viewModel.ImportPlan(WriteCsv(Header + "00:1D:9C:C7:B0:70,192.168.1.51,255.255.255.0,,,,,\r\n"));
        _viewModel.ImportPlan(WriteCsv(Header + "00:1D:9C:C7:B0:70,192.168.1.61,255.255.255.0,,,,,\r\n", "fixed.csv"));

        DeviceRowViewModel row = Assert.Single(_viewModel.Plan.Rows);
        Assert.Equal("192.168.1.61", row.IpText);
    }

    /// <summary>
    /// With an adapter selected the file is also checked against this machine, which is where an
    /// address off the segment gets caught before it reaches a device.
    /// </summary>
    [Fact]
    public void ChecksTheFileAgainstTheSelectedAdapter()
    {
        _viewModel.InterfaceBar.ApplyInventory([Nics.Healthy()]);
        _viewModel.InterfaceBar.SelectedAdapter = _viewModel.InterfaceBar.Adapters[0];

        _viewModel.ImportPlan(WriteCsv(Header + "00:1D:9C:C7:B0:70,10.0.0.51,255.255.255.0,,,,,\r\n"));

        Assert.Empty(_viewModel.Plan.Rows);
        Assert.Contains(
            _viewModel.Log.Entries,
            entry => entry.Message.Contains("192.168.1.0/24", StringComparison.Ordinal));
    }

    /// <summary>A file that is not there is a sentence, not an unhandled exception.</summary>
    [Fact]
    public void ReportsAFileItCannotRead()
    {
        _viewModel.ImportPlan(Path.Combine(_directory, "no-such-file.csv"));

        Assert.NotNull(_viewModel.ErrorMessage);
        Assert.Empty(_viewModel.Plan.Rows);
    }

    public void Dispose()
    {
        // MainViewModel owns the controller and the project it was handed.
        _viewModel.Dispose();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp folder is not worth failing a test over.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }

    private string WriteCsv(string text, string name = "plan.csv")
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }
}
