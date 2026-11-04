using System.Net;
using NetControl.Core.Persistence;
using NetControl.Core.Plan;
using Xunit;

namespace NetControl.Tests;

/// <summary>
/// The project IP workbook, read as a plan (PLAN-IMPORT.md F2).
///
/// <para>The arrangements are a cut-down Flint workbook: a Formula Builder tab naming the device
/// sheets and their columns, cell sheets with a switch table above a port map, and a grid tab that
/// must not be mistaken for a device list.</para>
/// </summary>
public sealed class PlanWorkbookTests
{
    private static readonly string?[] BuilderHeader =
        ["Include?", "Sheet name", "Name col", "IP col", "IP2 col", "First row", "Last row"];

    private static readonly string?[] PortMapHeader = ["Switch", "Port", "Chain", "Device", "IP", "IP2", "Notes"];

    [Fact]
    public void OffersTheSheetsTheFormulaBuilderIncludes()
    {
        PlanWorkbook workbook = Read(Flint());

        Assert.True(workbook.FromFormulaBuilder);
        Assert.Equal(new[] { "SF101", "SF102" }, workbook.Sheets.Select(s => s.Name));
    }

    /// <summary>
    /// The rules in one sheet: an IP2 is a second row, a spare port is skipped and counted, a switch
    /// listed in the switch table and again in the port map is one device, and every row knows where
    /// it is and which sheet row it came from.
    /// </summary>
    [Fact]
    public void ReadsACellSheetIntoPlanRows()
    {
        PlanSheet sheet = Read(Flint()).Sheets[0];

        Assert.Equal(4, sheet.DeviceCount);
        Assert.Equal(1, sheet.SecondPortCount);
        Assert.Equal(1, sheet.SkippedWithoutAddress);
        Assert.Equal(1, sheet.MergedRepeats);
        Assert.Equal(new[] { "120.15.106", "192.168.1", "192.168.5" }, sheet.Subnets);

        IReadOnlyList<PlanCsvRow> rows = sheet.ToRows(new Dictionary<string, PlanSubnetSettings>());

        PlanCsvRow robot = Assert.Single(rows, r => r.HostName == "SF101RC01");
        Assert.Equal(8, robot.LineNumber);
        Assert.Equal("192.168.1.51", robot.Ip);
        Assert.Equal("SF101 ES1 port 9", robot.PanelRef);
        Assert.Equal("IP2 = Port 2 of Robot", robot.Notes);
        Assert.Equal(string.Empty, robot.Mac);

        PlanCsvRow stream = Assert.Single(rows, r => r.HostName == "SF101RC01 (IP2)");
        Assert.Equal("192.168.5.1", stream.Ip);
        Assert.Equal(8, stream.LineNumber);

        Assert.Single(rows, r => r.HostName == "SF101ES1");
        Assert.DoesNotContain(rows, r => r.HostName == "Spare");
    }

    [Fact]
    public void GivesEachRowTheMaskAndGatewayOfItsSubnet()
    {
        PlanSheet sheet = Read(Flint()).Sheets[0];

        IReadOnlyList<PlanCsvRow> rows = sheet.ToRows(new Dictionary<string, PlanSubnetSettings>
        {
            ["192.168.1"] = new("255.255.255.0", "192.168.1.1"),
            ["120.15.106"] = new("255.255.254.0", string.Empty),
        });

        PlanCsvRow robot = Assert.Single(rows, r => r.HostName == "SF101RC01");
        Assert.Equal("255.255.255.0", robot.Mask);
        Assert.Equal("192.168.1.1", robot.Gateway);

        Assert.Equal("255.255.254.0", Assert.Single(rows, r => r.HostName == "SF101ES1").Mask);

        // Not given: no mask, which validation lets through and the grid shows amber.
        Assert.Equal(string.Empty, Assert.Single(rows, r => r.HostName == "SF101RC01 (IP2)").Mask);
    }

    /// <summary>
    /// The whole path a workbook takes: every row awaiting its MAC, validated like a CSV - so the
    /// same address under two names refuses the import and names the sheet row of each.
    /// </summary>
    [Fact]
    public void RefusesASheetThatPlansOneAddressTwice()
    {
        MemoryStream file = new XlsxBuilder()
            .Sheet(
                "SF101",
                PortMapHeader,
                ["ES1", "#4", "#1", "SF101SCC1.EN1", "120.15.106.135"],
                ["ES1", "#5", "#1", "SF101SCC1.EN2", "120.15.106.135"])
            .Build();

        IReadOnlyList<PlanCsvRow> rows = Read(file).Sheets[0].ToRows(
            new Dictionary<string, PlanSubnetSettings> { ["120.15.106"] = PlanSubnetSettings.Default });
        PlanValidationResult result = PlanValidation.Validate(rows);

        PlanProblem problem = Assert.Single(result.Problems);
        Assert.Equal(3, problem.LineNumber);
        Assert.Contains("already planned for SF101SCC1.EN1 on line 2", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ImportsACleanSheetAsRowsAwaitingTheirMac()
    {
        PlanSheet sheet = Read(Flint()).Sheets[0];

        PlanValidationResult result = PlanValidation.Validate(sheet.ToRows(
            sheet.Subnets.ToDictionary(s => s, _ => PlanSubnetSettings.Default)));

        Assert.True(result.IsValid);
        Assert.Equal(4, result.Devices.Count);
        Assert.All(result.Devices, device => Assert.True(device.IsAwaitingMac));
        Assert.Contains(result.Devices, d => d.PlannedIp!.Equals(IPAddress.Parse("192.168.5.1")));
    }

    /// <summary>
    /// Without a Formula Builder, any sheet with a device/IP header row is offered, wherever the
    /// header sits - and a grid tab, which has addresses but no such header, is not.
    /// </summary>
    [Fact]
    public void FindsDeviceSheetsByTheirHeaderWhenThereIsNoBuilder()
    {
        MemoryStream file = new XlsxBuilder()
            .Sheet("Network 192.168.1", ["Network 192.168.1.x"], [null, "192.168.1.1", null, "192.168.1.44"])
            .Sheet(
                "Panel A",
                ["Panel A - devices"],
                [],
                ["Name", "IP Address", "Notes"],
                ["Drive 1", "192.168.1.51", "conveyor 3"])
            .Build();

        PlanWorkbook workbook = Read(file);

        Assert.False(workbook.FromFormulaBuilder);
        PlanSheet sheet = Assert.Single(workbook.Sheets);
        Assert.Equal("Panel A", sheet.Name);

        PlanCsvRow row = Assert.Single(sheet.ToRows(new Dictionary<string, PlanSubnetSettings>()));
        Assert.Equal(4, row.LineNumber);
        Assert.Equal("Drive 1", row.HostName);
        Assert.Equal("conveyor 3", row.Notes);
        Assert.Equal("Panel A", row.PanelRef);
    }

    /// <summary>Text through the shared string table and numbers as plain digits - "9", never "9.0".</summary>
    [Fact]
    public void ReadsSharedStringsAndNumbersAsTheyLooked()
    {
        MemoryStream file = new XlsxBuilder()
            .Sheet("SF101", PortMapHeader, ["$ES1", "#9", "#1", "$SF101RC01", "$192.168.1.51"])
            .Build();

        PlanCsvRow row = Assert.Single(Read(file).Sheets[0].ToRows(new Dictionary<string, PlanSubnetSettings>()));

        Assert.Equal("SF101RC01", row.HostName);
        Assert.Equal("192.168.1.51", row.Ip);
        Assert.Equal("SF101 ES1 port 9", row.PanelRef);
    }

    [Fact]
    public void RefusesAFileThatIsNotAWorkbook()
    {
        using var file = new MemoryStream("Mac,Ip\r\n"u8.ToArray());

        var ex = Assert.Throws<PlanWorkbookException>(() => Read(file));

        Assert.Contains("not an Excel workbook", ex.Message, StringComparison.Ordinal);
        Assert.NotNull(ex.Remediation);
    }

    [Fact]
    public void RefusesAWorkbookWithNoDeviceSheet()
    {
        MemoryStream file = new XlsxBuilder().Sheet("Notes", ["Revision history"], ["Rev1", "2026-10-28"]).Build();

        var ex = Assert.Throws<PlanWorkbookException>(() => Read(file));

        Assert.Contains("No sheet in that workbook lists devices", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("A", 1)]
    [InlineData("f", 6)]
    [InlineData("Z", 26)]
    [InlineData("AA", 27)]
    [InlineData("AB", 28)]
    [InlineData("", 0)]
    [InlineData("A1", 0)]
    public void ReadsAColumnLetter(string letters, int expected) =>
        Assert.Equal(expected, XlsxWorkbook.ColumnNumber(letters));

    /// <summary>
    /// Formula Builder includes SF101 and SF102, excludes SF103 (Include = No) and names a sheet that
    /// does not exist. SF101 has a switch table, then its port map from row 5; SF102 is minimal.
    /// </summary>
    private static MemoryStream Flint() =>
        new XlsxBuilder()
            .Sheet(
                "Formula Builder",
                ["Local Network - Source Sheets"],
                BuilderHeader,
                ["Yes", "SF101", "D", "E", "F", "#5", "#300"],
                ["Yes", "SF102", "D", "E", "F", "#2", "#300"],
                ["No", "SF103", "D", "E", "F", "#2", "#300"],
                ["Yes", "SF104", "D", "E", "F", "#2", "#300"])
            .Sheet(
                "SF101",
                ["SF101 - Ethernet Switch / Port Structure"],
                ["Switch", "Type", "Mgmt IP"],
                ["ES1", "Managed ES", "120.15.106.13"],
                PortMapHeader,
                ["ES1", null, null, "SF101ES1", "120.15.106.13"],
                ["ES1", "#3", "#1", "SF101ES1", "120.15.106.13"],
                ["ES1", "#4", "#1", "SF101SCC1.EN1", "120.15.106.135", null, "PC Paintworks"],
                ["ES1", "#9", "#1", "SF101RC01", "192.168.1.51", "192.168.5.1", "IP2 = Port 2 of Robot"],
                ["ES1", "#13", "#1", "Spare"])
            .Sheet("SF102", PortMapHeader, ["ES1", "#9", "#1", "SF102RC01", "192.168.1.55"])
            .Sheet("SF103", PortMapHeader, ["ES1", "#9", "#1", "SF103RC01", "192.168.1.59"])
            .Sheet("Network 192.168.1", ["Network 192.168.1.x"], [null, "192.168.1.1"])
            .Build();

    // ---- Rev3 layout: Final / Temp IP1 / IP2, IP2 Type, Dev Port; no Formula Builder ----

    private static readonly string?[] Rev3Header =
        ["Switch", "Port", "Chain", "Device", "Dev Port", "Final IP1", "Final IP2", "IP2 Type", "Temp IP1", "Temp IP2", "Status", "Notes"];

    /// <summary>
    /// Rev3 has no Formula Builder. Its cell sheets are found by "Final IP1"; the views built from
    /// them - a per-subnet Net tab, the IP List, the hidden Calc tab - all have a device/IP header
    /// too, mix every cell, and must not be offered.
    /// </summary>
    [Fact]
    public void OffersOnlyTheRev3CellSheets()
    {
        PlanWorkbook workbook = Read(Rev3());

        Assert.False(workbook.FromFormulaBuilder);
        Assert.Equal(new[] { "SF101", "SF102" }, workbook.Sheets.Select(s => s.Name));
    }

    /// <summary>A hidden tab is plumbing, even when it is the only sheet with a device header.</summary>
    [Fact]
    public void NeverOffersAHiddenSheet()
    {
        MemoryStream file = new XlsxBuilder()
            .HiddenSheet("Calc", ["IP", "Device"], ["192.168.1.5", "DV200TP1"])
            .Build();

        Assert.Throws<PlanWorkbookException>(() => Read(file));
    }

    /// <summary>
    /// A Temp address is only filled while the device is running on it, so it is the address planned;
    /// the Final one goes in the notes. Without a Temp, the Final is planned.
    /// </summary>
    [Fact]
    public void PlansTheTempAddressWhileThereIsOne()
    {
        IReadOnlyList<PlanCsvRow> rows = Rows(Read(Rev3()).Sheets[0]);

        PlanCsvRow robot = Assert.Single(rows, r => r.HostName == "SF101RC01");
        Assert.Equal("192.168.1.51", robot.Ip);
        Assert.Equal("Port 2 = streaming motion | Temp address - final 120.15.106.139", robot.Notes);

        PlanCsvRow motion = Assert.Single(rows, r => r.HostName == "SF101RC01 (IP2)");
        Assert.Equal("192.168.5.1", motion.Ip);
        Assert.Equal("Port 2 = streaming motion", motion.Notes);

        Assert.Equal("120.15.106.13", Assert.Single(rows, r => r.HostName == "SF101ES1").Ip);
        // The robot's IP1 and the fieldbus port's IP1; the fieldbus slave's temp is a same-port IP2.
        Assert.Equal(2, Read(Rev3()).Sheets[0].TemporaryAddresses);
    }

    /// <summary>
    /// "Same port - Slave" is a second address on the same MAC. BOOTP gives one MAC one address, so it
    /// is not a row of its own: it is recorded on the main row.
    /// </summary>
    [Fact]
    public void KeepsASamePortIp2InTheNotesRatherThanAsARow()
    {
        PlanSheet sheet = Read(Rev3()).Sheets[0];
        IReadOnlyList<PlanCsvRow> rows = Rows(sheet);

        PlanCsvRow fieldbus = Assert.Single(rows, r => r.Ip == "192.168.1.120");
        Assert.Equal("SF101PCE01 RC1 3M", fieldbus.HostName);
        Assert.Equal(
            "Slave virtual fieldbus | Temp address - final not assigned | Also 192.168.1.1 on the same port (Same port - Slave)",
            fieldbus.Notes);
        Assert.DoesNotContain(rows, r => r.Ip == "192.168.1.1");
        Assert.Equal(1, sheet.SamePortAddresses);
        Assert.Equal(1, sheet.SecondPortCount);
    }

    /// <summary>
    /// A row whose only address is a same-port IP2 has nothing else to be: that address is the row's.
    /// </summary>
    [Fact]
    public void PlansASamePortIp2WhenItIsTheOnlyAddress()
    {
        MemoryStream file = new XlsxBuilder()
            .Sheet("SF101", Rev3Header, ["ES2", "#2", "#1", "SF101PCE01 RC1 3M", "Fieldbus", null, "192.168.1.130", "Same port - Slave"])
            .Build();

        PlanCsvRow row = Assert.Single(Rows(Read(file).Sheets[0]));

        Assert.Equal("192.168.1.130", row.Ip);
        Assert.Equal("SF101PCE01 RC1 3M", row.HostName);
    }

    /// <summary>Four ports of one 3M controller share its name; Dev Port tells them apart.</summary>
    [Fact]
    public void PutsTheDevPortInThePanelRef()
    {
        IReadOnlyList<PlanCsvRow> rows = Rows(Read(Rev3()).Sheets[0]);

        Assert.Equal("SF101 ES2 port 2 (Fieldbus)", Assert.Single(rows, r => r.Ip == "192.168.1.120").PanelRef);
        Assert.Equal("SF101 Direct (Motion)", Assert.Single(rows, r => r.Ip == "192.168.5.21").PanelRef);
        Assert.Equal("SF101 ES1 port 9", Assert.Single(rows, r => r.HostName == "SF101RC01").PanelRef);
    }

    [Fact]
    public void ImportsACleanRev3SheetAsRowsAwaitingTheirMac()
    {
        PlanSheet sheet = Read(Rev3()).Sheets[0];

        PlanValidationResult result = PlanValidation.Validate(sheet.ToRows(
            sheet.Subnets.ToDictionary(s => s, _ => PlanSubnetSettings.Default)));

        Assert.True(result.IsValid);
        Assert.Equal(5, result.Devices.Count);
        Assert.All(result.Devices, device => Assert.True(device.IsAwaitingMac));
    }

    /// <summary>
    /// A cut-down Rev3 workbook: a Read Me, two cell sheets with a switch table above the port map
    /// (header on row 5 here), a per-subnet Net tab, the IP List and a hidden Calc tab - the last
    /// three each with an IP / Device header of their own.
    /// </summary>
    private static MemoryStream Rev3() =>
        new XlsxBuilder()
            .Sheet("Read Me", ["Flint IP Addresses - Rev3"], ["Final IP1", "The device's permanent address"])
            .Sheet(
                "SF101",
                ["SF101 - Ethernet Switch / Port Map"],
                ["Switch", "Type", "# Ports", "Uplink", "Mgmt IP - Final (auto)"],
                ["ES1", "Managed ES", "#24", "Root", "120.15.106.13"],
                [],
                Rev3Header,
                ["ES1", null, null, "SF101ES1", null, "120.15.106.13", null, null, null, null, "Temp IP needed"],
                ["ES1", "#9", "#1", "SF101RC01", null, "120.15.106.139", "192.168.5.1", "Port 2 - Motion", "192.168.1.51", null, "On temp", "Port 2 = streaming motion"],
                ["ES1", "#13", "#1", "Spare"],
                ["ES2", "#2", "#1", "SF101PCE01 RC1 3M", "Fieldbus", null, null, "Same port - Slave", "192.168.1.120", "192.168.1.1", "On temp", "Slave virtual fieldbus"],
                ["Direct", null, null, "SF101PCE01 RC1 3M", "Motion", "192.168.5.21", null, null, null, null, "Final", "-> SF101RC01 port 2"],
                ["Direct", null, null, "SF101PCE01 RC1 3M", "EtherCAT"])
            .Sheet("SF102", Rev3Header, ["ES1", null, null, "SF102ES1", null, "120.15.106.13"])
            .Sheet("Net 192.168.1", ["Net 192.168.1"], [], ["IP", "Device"], ["192.168.1.51", "SF101RC01"])
            .Sheet("IP List", ["IP List"], ["#", "IP Address", "Device", "Dev Port", "Sheet"], ["#1", "120.15.106.13", "SF101ES1", null, "SF101"])
            .HiddenSheet("Calc", ["IP", "Device", "Sheet"], ["120.15.106.13", "SF101ES1", "SF101"])
            .Build();

    private static IReadOnlyList<PlanCsvRow> Rows(PlanSheet sheet) =>
        sheet.ToRows(new Dictionary<string, PlanSubnetSettings>());

    private static PlanWorkbook Read(MemoryStream file)
    {
        using (file)
        {
            return PlanWorkbook.Read(file);
        }
    }
}
