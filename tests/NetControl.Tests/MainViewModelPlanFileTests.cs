using System.Net;
using System.Text;
using NetControl.App.Serving;
using NetControl.App.ViewModels;
using NetControl.Core;
using NetControl.Core.Dhcp;
using NetControl.Core.Oui;
using NetControl.Core.Persistence;
using NetControl.Core.Plan;
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
        _viewModel.ImportPlan(WriteCsv("HostName,Mask\r\nconveyor-3,255.255.255.0\r\n"));

        Assert.Contains("neither a MAC nor an IP column", _viewModel.ErrorMessage ?? string.Empty, StringComparison.Ordinal);
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

    // ---- Project IP workbooks and rows awaiting their MAC (PLAN-IMPORT.md F2, F3) -------------

    /// <summary>
    /// One sheet in, every row awaiting its MAC: in the grid, not in the policy - nothing is
    /// servable until a MAC arrives - and in the record with the sheet named.
    /// </summary>
    [Fact]
    public void ImportsOneWorkbookSheetAsRowsAwaitingTheirMac()
    {
        string path = WriteWorkbook(Cell("SF101", "192.168.1.51", "192.168.1.52"));

        PlanWorkbook? workbook = _viewModel.ReadWorkbook(path);
        Assert.NotNull(workbook);
        PlanSheet sheet = Assert.Single(workbook.Sheets);

        _viewModel.ImportWorkbookSheet(path, sheet, Masks(sheet));

        Assert.Null(_viewModel.ErrorMessage);
        Assert.Equal(2, _viewModel.Plan.Rows.Count);
        Assert.All(_viewModel.Plan.Rows, row => Assert.Equal(string.Empty, row.MacText));
        Assert.Equal(0, _viewModel.PlannedDeviceCount);
        Assert.Equal(2, _viewModel.Plan.AwaitingMac().Count);

        EventRecord row = Assert.Single(_project.Events.All(), e => e.Category == EventCategory.App);
        Assert.Contains("Sheet SF101", row.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two devices on one address refuse the sheet, and the problem says which sheet the row
    /// numbers are on - "line 6" means nothing without it.
    /// </summary>
    [Fact]
    public void RefusesAWorkbookSheetWithADuplicateAndNamesTheSheet()
    {
        string path = WriteWorkbook(Cell("SF101", "120.15.106.135", "120.15.106.135"));
        PlanSheet sheet = Assert.Single(_viewModel.ReadWorkbook(path)!.Sheets);

        _viewModel.ImportWorkbookSheet(path, sheet, Masks(sheet));

        Assert.Empty(_viewModel.Plan.Rows);
        Assert.Contains("SF101 sheet", _viewModel.ErrorRemediation ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains(_viewModel.Log.Entries, e => e.Message.StartsWith("Import SF101: line 3:", StringComparison.Ordinal));
    }

    /// <summary>
    /// A workbook plans several subnets at once, so the adapter's own subnet is not held against it
    /// the way it is for a CSV - otherwise no real workbook would ever import.
    /// </summary>
    [Fact]
    public void ImportsAWorkbookSheetThatSpansSubnetsTheAdapterIsNotOn()
    {
        _viewModel.InterfaceBar.ApplyInventory([Nics.Healthy()]);
        _viewModel.InterfaceBar.SelectedAdapter = _viewModel.InterfaceBar.Adapters[0];

        string path = WriteWorkbook(Cell("SF101", "120.15.106.13", "192.168.5.1"));
        PlanSheet sheet = Assert.Single(_viewModel.ReadWorkbook(path)!.Sheets);

        _viewModel.ImportWorkbookSheet(path, sheet, Masks(sheet));

        Assert.Null(_viewModel.ErrorMessage);
        Assert.Equal(2, _viewModel.Plan.Rows.Count);
    }

    [Fact]
    public void SaysWhyAFileIsNotAWorkbook()
    {
        Assert.Null(_viewModel.ReadWorkbook(WriteCsv(Header, "plan.xlsx")));
        Assert.Contains("not an Excel workbook", _viewModel.ErrorMessage ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>
    /// The request-log path: a new device asks, the user picks the row it is, and the MAC lands on
    /// that row - which is now servable, and the fill is in the record.
    /// </summary>
    [Fact]
    public void FillsAnAwaitingRowsMacFromARequest()
    {
        ImportAwaiting("192.168.1.51");
        LogEntryViewModel request = Request(Drive);

        IReadOnlyList<AwaitingRowOption>? options = _viewModel.AwaitingRowsFor(request);
        Assert.NotNull(options);
        AwaitingRowOption option = Assert.Single(options);
        Assert.Equal("SF101RC01", option.Name);

        Assert.True(_viewModel.AssignToAwaitingRow(request, option.Row));

        Assert.Equal(Drive, option.Row.Mac);
        Assert.Equal(1, _viewModel.PlannedDeviceCount);
        Assert.True(_policy.TryGet(Drive, out _));
        Assert.Empty(_viewModel.Plan.AwaitingMac());
        Assert.Contains(_project.Events.All(), e => e.Message.StartsWith("MAC filled in from the request log", StringComparison.Ordinal));
    }

    /// <summary>A device already in the plan is not offered rows: it would end up on two.</summary>
    [Fact]
    public void OffersNoAwaitingRowsToADeviceAlreadyPlanned()
    {
        _viewModel.ImportPlan(WriteCsv(Header + "00:1D:9C:C7:B0:70,192.168.1.60,255.255.255.0,,,,,\r\n"));
        ImportAwaiting("192.168.1.51");

        Assert.Null(_viewModel.AwaitingRowsFor(Request(Drive)));
        Assert.Single(_viewModel.Plan.AwaitingMac());
    }

    [Fact]
    public void SaysSoWhenNoRowIsAwaitingAMac()
    {
        Assert.Null(_viewModel.AwaitingRowsFor(Request(Drive)));
        Assert.Contains(_viewModel.Log.Entries, e => e.Message == "No plan row is awaiting a MAC.");
    }

    /// <summary>
    /// The scan path: a device sitting at an awaiting row's address is paired with it, an address
    /// two devices answered on is not, and only the pairings shown are written.
    /// </summary>
    [Fact]
    public void FillsInTheAwaitingMacsAScanFound()
    {
        ImportAwaiting("192.168.1.51", "192.168.1.52");

        MacAddress other = MacAddress.Parse("00:1D:9C:C7:B0:71");
        MacAddress third = MacAddress.Parse("00:1D:9C:C7:B0:72");
        _viewModel.Scan.Rows.Add(Answering("192.168.1.51", Drive));
        _viewModel.Scan.Rows.Add(Answering("192.168.1.52", other));
        _viewModel.Scan.Rows.Add(Answering("192.168.1.52", third));

        IReadOnlyList<AwaitingMacMatch> matches = _viewModel.AwaitingMacMatches();

        AwaitingMacMatch match = Assert.Single(matches);
        Assert.Equal("SF101RC01", match.RowName);
        Assert.Equal(Drive, match.Result.Mac);

        Assert.Equal(1, _viewModel.FillMacsFromScan(matches));
        Assert.Equal(Drive, match.Row.Mac);
        Assert.Single(_viewModel.Plan.AwaitingMac());
    }

    [Fact]
    public void FillsInOneMacFromAScanResult()
    {
        ImportAwaiting("192.168.1.51");

        Assert.True(_viewModel.FillMacFromScan(Answering("192.168.1.51", Drive)));
        Assert.Empty(_viewModel.Plan.AwaitingMac());

        // Nothing awaiting there any more: said, not done twice.
        Assert.False(_viewModel.FillMacFromScan(Answering("192.168.1.51", MacAddress.Parse("00:1D:9C:C7:B0:71"))));
    }

    private static ScanResultViewModel Answering(string address, MacAddress mac) =>
        new(new NetControl.Core.Discovery.DiscoveredDevice
        {
            Identity = Identities.Reply(address: address),
            Mac = mac,
            MacSource = NetControl.Core.Discovery.MacSource.Arp,
        });

    private void ImportAwaiting(params string[] ips)
    {
        string path = WriteWorkbook(Cell("SF101", ips));
        PlanSheet sheet = Assert.Single(_viewModel.ReadWorkbook(path)!.Sheets);
        _viewModel.ImportWorkbookSheet(path, sheet, Masks(sheet));
        Assert.Null(_viewModel.ErrorMessage);
    }

    private static LogEntryViewModel Request(MacAddress mac) =>
        LogEntryViewModel.FromRequest(
            Events.Request(mac, AssignmentDecision.Ignore("not in the plan")),
            vendor: null,
            planned: null,
            DhcpServerMode.Watch,
            selectedInterfaceIndex: null);

    /// <summary>A cell sheet with one robot per address, SF101RC01 first.</summary>
    private static XlsxBuilder Cell(string name, params string[] ips)
    {
        List<string?[]> rows = [["Switch", "Port", "Chain", "Device", "IP", "IP2", "Notes"]];
        for (int i = 0; i < ips.Length; i++)
        {
            rows.Add(["ES1", $"#{i + 9}", "#1", $"{name}RC0{i + 1}", ips[i]]);
        }

        return new XlsxBuilder().Sheet(name, [.. rows]);
    }

    private static Dictionary<string, PlanSubnetSettings> Masks(PlanSheet sheet) =>
        sheet.Subnets.ToDictionary(prefix => prefix, _ => PlanSubnetSettings.Default);

    private string WriteWorkbook(XlsxBuilder builder, string name = "flint.xlsx")
    {
        string path = Path.Combine(_directory, name);
        using MemoryStream built = builder.Build();
        File.WriteAllBytes(path, built.ToArray());
        return path;
    }

    private string WriteCsv(string text, string name = "plan.csv")
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }
}
