using NetControl.Core.Plan;
using Xunit;

namespace NetControl.Tests;

/// <summary>Plan rows to and from the tab-separated text a spreadsheet copies.</summary>
public class PlanClipboardTests
{
    [Fact]
    public void ReadsBackWhatItWrites()
    {
        string text = PlanClipboard.Write(
        [
            ["00:1D:9C:C7:B0:70", "192.168.1.51", "255.255.255.0", "", "drive1", "CP-101", "VFD", "line one\nline two"],
            ["", "192.168.1.52", "255.255.255.0", "192.168.1.1", "", "CP-101", "Remote I/O", "has a\ttab and \"quotes\""],
        ]);

        IReadOnlyList<PlanCsvRow> rows = PlanClipboard.Read(text);

        Assert.Equal(2, rows.Count);
        Assert.Equal("00:1D:9C:C7:B0:70", rows[0].Mac);
        Assert.Equal("drive1", rows[0].HostName);
        Assert.Equal("", rows[1].Mac);
        Assert.Equal("192.168.1.1", rows[1].Gateway);
        Assert.Equal("Remote I/O", rows[1].Role);
    }

    /// <summary>Fields with tabs, line breaks and quotes are quoted the way Excel quotes them, and survive.</summary>
    [Fact]
    public void QuotesAwkwardFieldsTheWayExcelDoes()
    {
        string text = PlanClipboard.Write([["", "192.168.1.52", "", "", "", "", "", "a\tb\r\nc \"d\""]]);

        Assert.Contains("\"a\tb\r\nc \"\"d\"\"\"", text, StringComparison.Ordinal);
        Assert.Equal("a\tb\r\nc \"d\"", Assert.Single(PlanClipboard.Read(text)).Notes);
    }

    [Fact]
    public void MatchesColumnsByHeaderNameInAnyOrder()
    {
        IReadOnlyList<PlanCsvRow> rows = PlanClipboard.Read(
            "IP Address\tDescription\tMAC\r\n192.168.1.51\tVFD\t00:1D:9C:C7:B0:70\r\n");

        PlanCsvRow row = Assert.Single(rows);
        Assert.Equal("192.168.1.51", row.Ip);
        Assert.Equal("VFD", row.Role);
        Assert.Equal("00:1D:9C:C7:B0:70", row.Mac);
    }

    /// <summary>Without a header the grid's own order is assumed: MAC first.</summary>
    [Fact]
    public void WithoutAHeaderTakesTheGridsOrder()
    {
        PlanCsvRow row = Assert.Single(PlanClipboard.Read("00:1D:9C:C7:B0:70\t192.168.1.51\t255.255.255.0\n"));

        Assert.Equal("00:1D:9C:C7:B0:70", row.Mac);
        Assert.Equal("192.168.1.51", row.Ip);
        Assert.Equal("255.255.255.0", row.Mask);
    }

    /// <summary>
    /// Address, mask and gateway copied out of a workbook have no MAC column. Read as MACs every row
    /// would turn red, so a first column of nothing but addresses is taken to start at IP.
    /// </summary>
    [Fact]
    public void ABlockStartingWithAddressesStartsAtTheIpColumn()
    {
        IReadOnlyList<PlanCsvRow> rows = PlanClipboard.Read(
            "120.15.106.21\t255.255.255.0\t120.15.106.1\r\n120.15.106.22\t255.255.255.0\t120.15.106.1\r\n");

        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal(string.Empty, row.Mac));
        Assert.Equal("120.15.106.22", rows[1].Ip);
        Assert.Equal("120.15.106.1", rows[1].Gateway);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\r\n\t\r\n")]
    public void NothingOnTheClipboardIsNoRows(string? text) => Assert.Empty(PlanClipboard.Read(text));
}
