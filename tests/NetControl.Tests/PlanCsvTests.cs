using System.Net;
using NetControl.Core;
using NetControl.Core.Persistence;
using NetControl.Core.Plan;
using Xunit;

namespace NetControl.Tests;

/// <summary>
/// The CSV reader and writer, at the level of the file's shape only. What the cells mean is
/// <see cref="PlanValidationTests"/>.
///
/// <para>The tests that matter most here are the quoting ones. A parser that splits on commas
/// looks correct against every file somebody writes by hand while testing, and then shifts every
/// column of the first real plan whose Notes column says "drive, panel 3" - putting the mask in the
/// gateway with nothing at all to indicate it happened.</para>
///
/// <para>Every fixture here is built with explicit <c>\r\n</c> rather than a raw string literal, so
/// that what is being tested is the reader rather than whatever line endings this file was last
/// saved with.</para>
/// </summary>
public sealed class PlanCsvTests
{
    private static readonly MacAddress Mac = MacAddress.Parse("00:1D:9C:C7:B0:70");

    [Fact]
    public void RoundTripsAPlanThroughTheWriterAndTheReader()
    {
        var device = new DeviceRecord
        {
            Mac = Mac,
            PlannedIp = IPAddress.Parse("192.168.1.51"),
            PlannedMask = IPAddress.Parse("255.255.255.0"),
            PlannedGateway = IPAddress.Parse("192.168.1.1"),
            HostName = "aent-01",
            PanelRef = "MCC-3",
            Role = "Conveyor 3 drive",
            Notes = "Swapped 2026-02-11",
        };

        IReadOnlyList<PlanCsvRow> rows = PlanCsv.Read(PlanCsv.Write([device]));

        PlanCsvRow row = Assert.Single(rows);
        Assert.Equal("00:1D:9C:C7:B0:70", row.Mac);
        Assert.Equal("192.168.1.51", row.Ip);
        Assert.Equal("255.255.255.0", row.Mask);
        Assert.Equal("192.168.1.1", row.Gateway);
        Assert.Equal("aent-01", row.HostName);
        Assert.Equal("MCC-3", row.PanelRef);
        Assert.Equal("Conveyor 3 drive", row.Role);
        Assert.Equal("Swapped 2026-02-11", row.Notes);
    }

    /// <summary>
    /// The reason the format is not "split on commas". All three of these turn up in a Notes
    /// column, and all three survive a round trip.
    /// </summary>
    [Fact]
    public void CarriesCommasQuotesAndNewlinesThroughTheNotesColumn()
    {
        const string Awkward = "Replaced drive, panel 3.\nOld one said \"F5 comms loss\".";

        var device = new DeviceRecord { Mac = Mac, Notes = Awkward };

        string text = PlanCsv.Write([device]);
        PlanCsvRow row = Assert.Single(PlanCsv.Read(text));

        Assert.Equal(Awkward, row.Notes);

        // And the file really is quoted, rather than the reader having been lenient about a file
        // the writer wrote wrong. A doubled quote is what RFC 4180 says an embedded one looks like.
        Assert.Contains("\"\"F5 comms loss\"\"", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A quoted field spanning lines does not renumber the rows after it - and the line reported
    /// for a row is the line it starts on, which is where a text editor's cursor needs to go.
    /// </summary>
    [Fact]
    public void ReportsTheLineARowStartedOnEvenWhenAFieldSpansLines()
    {
        string text = "Mac,Ip,Notes\r\n"
            + "00:1D:9C:C7:B0:70,192.168.1.51,\"line one\r\nline two\"\r\n"
            + "00:1D:9C:C7:B0:71,192.168.1.52,plain\r\n";

        IReadOnlyList<PlanCsvRow> rows = PlanCsv.Read(text);

        Assert.Equal(2, rows.Count);
        Assert.Equal(2, rows[0].LineNumber);
        Assert.Equal("line one\r\nline two", rows[0].Notes);

        // The row after a two-line field is on line 4, not line 3. Getting this wrong sends
        // somebody to the wrong line of a hundred-row file.
        Assert.Equal(4, rows[1].LineNumber);
    }

    /// <summary>
    /// Columns are matched by meaning, not by position, so the spreadsheet somebody already has
    /// imports without being rearranged first.
    /// </summary>
    [Fact]
    public void MatchesColumnsByNameInAnyOrderAndAnySpelling()
    {
        string text = "Panel,IP Address,MAC address,Subnet Mask\r\n"
            + "MCC-3,192.168.1.51,00:1D:9C:C7:B0:70,255.255.255.0\r\n";

        PlanCsvRow row = Assert.Single(PlanCsv.Read(text));

        Assert.Equal("00:1D:9C:C7:B0:70", row.Mac);
        Assert.Equal("192.168.1.51", row.Ip);
        Assert.Equal("255.255.255.0", row.Mask);
        Assert.Equal("MCC-3", row.PanelRef);
    }

    /// <summary>
    /// A plan sheet also carries part numbers and PO references. Refusing the file over a column
    /// the tool does not need would be pedantry - but the unknown column must keep its place, or
    /// every column after it shifts by one.
    /// </summary>
    [Fact]
    public void IgnoresColumnsItDoesNotKnowWithoutShiftingTheOnesItDoes()
    {
        string text = "Mac,Part number,PO,Ip,Mask\r\n"
            + "00:1D:9C:C7:B0:70,1734-AENT,PO-4471,192.168.1.51,255.255.255.0\r\n";

        PlanCsvRow row = Assert.Single(PlanCsv.Read(text));

        Assert.Equal("192.168.1.51", row.Ip);
        Assert.Equal("255.255.255.0", row.Mask);
    }

    /// <summary>Trailing empty cells are what a spreadsheet drops; that is not a malformed file.</summary>
    [Fact]
    public void AcceptsARowShorterThanItsHeader()
    {
        string text = "Mac,Ip,Mask,Gateway,HostName,PanelRef,Role,Notes\r\n"
            + "00:1D:9C:C7:B0:70,192.168.1.51\r\n";

        PlanCsvRow row = Assert.Single(PlanCsv.Read(text));

        Assert.Equal("192.168.1.51", row.Ip);
        Assert.Equal(string.Empty, row.Mask);
        Assert.Equal(string.Empty, row.Notes);
    }

    /// <summary>
    /// A file that has been through a spreadsheet often ends with a few empty rows. Reporting eight
    /// problems against each of them would bury the one real problem above.
    /// </summary>
    [Fact]
    public void SkipsBlankRows()
    {
        string text = "Mac,Ip\r\n\r\n00:1D:9C:C7:B0:70,192.168.1.51\r\n,\r\n\r\n";

        PlanCsvRow row = Assert.Single(PlanCsv.Read(text));

        Assert.Equal("00:1D:9C:C7:B0:70", row.Mac);
    }

    /// <summary>The mark a spreadsheet leaves behind must not become part of the first column's name.</summary>
    [Fact]
    public void ReadsAFileThatStartsWithAByteOrderMark()
    {
        string text = "\uFEFF" + "Mac,Ip\r\n00:1D:9C:C7:B0:70,192.168.1.51\r\n";

        PlanCsvRow row = Assert.Single(PlanCsv.Read(text));

        Assert.Equal("00:1D:9C:C7:B0:70", row.Mac);
    }

    /// <summary>
    /// Without a MAC column there is nothing to key a plan on, and every row would come back
    /// complaining about a missing MAC. Say the real thing once instead.
    /// </summary>
    [Fact]
    public void RefusesAFileWithNoMacColumn()
    {
        PlanCsvException ex = Assert.Throws<PlanCsvException>(
            () => PlanCsv.Read("Ip,Mask\r\n192.168.1.51,255.255.255.0\r\n"));

        Assert.Contains("no MAC column", ex.Message, StringComparison.Ordinal);

        // The header it actually saw, so the person can see which column they meant to be the MAC.
        Assert.Contains("'Ip'", ex.Message, StringComparison.Ordinal);
        Assert.NotNull(ex.Remediation);
    }

    /// <summary>Two columns claiming to be the same one: refuse, rather than let one silently win.</summary>
    [Fact]
    public void RefusesAHeaderThatNamesOneColumnTwice()
    {
        PlanCsvException ex = Assert.Throws<PlanCsvException>(
            () => PlanCsv.Read("Mac,MAC Address,Ip\r\n"));

        Assert.Contains("twice", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The one malformed thing the reader will not be lenient about. Swallowing the rest of the
    /// file would return a short plan that looks complete, which is the failure this tool exists to
    /// stop happening in other tools.
    /// </summary>
    [Fact]
    public void RefusesAQuoteThatIsNeverClosed()
    {
        PlanCsvException ex = Assert.Throws<PlanCsvException>(
            () => PlanCsv.Read("Mac,Notes\r\n00:1D:9C:C7:B0:70,\"never ends\r\n"));

        Assert.Contains("line 2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesAnEmptyFile() => Assert.Throws<PlanCsvException>(() => PlanCsv.Read(string.Empty));

    /// <summary>
    /// A stray quote inside an unquoted field is content. A spreadsheet writing <c>3" conduit</c>
    /// unquoted is a file worth importing, not one worth refusing.
    /// </summary>
    [Fact]
    public void TakesAStrayQuoteInsideAnUnquotedFieldLiterally()
    {
        PlanCsvRow row = Assert.Single(PlanCsv.Read("Mac,Notes\r\n00:1D:9C:C7:B0:70,3\" conduit\r\n"));

        Assert.Equal("3\" conduit", row.Notes);
    }

    /// <summary>
    /// Line endings are fixed on write rather than taken from the machine, so the same plan
    /// exported twice on two laptops is the same file. A diff that is entirely line endings is a
    /// diff nobody reads.
    /// </summary>
    [Fact]
    public void WritesCrlfWhateverTheMachineUses()
    {
        string text = PlanCsv.Write([new DeviceRecord { Mac = Mac }]);

        Assert.StartsWith("Mac,Ip,Mask,Gateway,HostName,PanelRef,Role,Notes\r\n", text, StringComparison.Ordinal);
        Assert.EndsWith("\r\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\n\n", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A row with a MAC and no address is a normal thing to export: it is what "Add to plan" from
    /// the log produces, and the tool deliberately does not invent an address for it.
    /// </summary>
    [Fact]
    public void ExportsARowThatHasNoAddressYet()
    {
        string text = PlanCsv.Write([new DeviceRecord { Mac = Mac, Role = "Conveyor 3 drive" }]);

        PlanCsvRow row = Assert.Single(PlanCsv.Read(text));
        Assert.Equal(string.Empty, row.Ip);
        Assert.Equal("Conveyor 3 drive", row.Role);
    }

    /// <summary>
    /// Vendor is not a column, on purpose - it is resolved from the MAC at import. A file that has
    /// one is read anyway, and the column is ignored rather than becoming a stray Notes field.
    /// </summary>
    [Fact]
    public void DoesNotWriteAVendorColumnAndIgnoresOneItIsGiven()
    {
        Assert.DoesNotContain("Vendor", string.Join(",", PlanCsv.Header), StringComparison.Ordinal);

        PlanCsvRow row = Assert.Single(
            PlanCsv.Read("Mac,Vendor,Notes\r\n00:1D:9C:C7:B0:70,Someone Else Ltd,kept\r\n"));

        Assert.Equal("kept", row.Notes);
    }
}
