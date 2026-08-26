using System.Net;
using NetControl.Core;
using NetControl.Core.Persistence;
using NetControl.Core.Plan;
using Xunit;

namespace NetControl.Tests;

/// <summary>
/// The whole-file pass that runs before a plan is written.
///
/// <para>The rule under test throughout is that <b>a file with one bad row imports nothing</b>. A
/// partial import leaves somebody at a panel with some devices planned and some not, and no way to
/// tell which apart from reading the file against the grid row by row. Every test that arranges a
/// bad row therefore also checks that the good rows beside it did not go in.</para>
/// </summary>
public sealed class PlanValidationTests
{
    private const string Header = "Mac,Ip,Mask,Gateway,HostName,PanelRef,Role,Notes\r\n";

    private const string GoodRow = "00:1D:9C:C7:B0:70,192.168.1.51,255.255.255.0,,,,,\r\n";

    [Fact]
    public void AcceptsACompleteRow()
    {
        PlanValidationResult result = Validate(GoodRow);

        Assert.True(result.IsValid);
        DeviceRecord device = Assert.Single(result.Devices);
        Assert.Equal(MacAddress.Parse("00:1D:9C:C7:B0:70"), device.Mac);
        Assert.Equal(IPAddress.Parse("192.168.1.51"), device.PlannedIp);
        Assert.Equal(IPAddress.Parse("255.255.255.0"), device.PlannedMask);
    }

    /// <summary>
    /// A MAC read off a label at 7am with the address still to be decided is a normal row, and the
    /// grid shows it amber until it has one. Refusing it here would make import stricter than the
    /// grid, for no reason anybody could act on.
    /// </summary>
    [Fact]
    public void AcceptsARowWithNoAddressYet()
    {
        PlanValidationResult result = Validate("00:1D:9C:C7:B0:70,,,,,,Conveyor 3 drive,\r\n");

        Assert.True(result.IsValid);
        DeviceRecord device = Assert.Single(result.Devices);
        Assert.Null(device.PlannedIp);
        Assert.Equal("Conveyor 3 drive", device.Role);
    }

    /// <summary>The vendor comes off the MAC here, once, which is why the file has no vendor column.</summary>
    [Fact]
    public void ResolvesTheVendorFromTheMacAtImport()
    {
        PlanValidationResult result = PlanValidation.Validate(
            PlanCsv.Read(Header + GoodRow),
            new PlanImportContext { Oui = new FakeOuiLookup("Rockwell Automation") });

        Assert.Equal("Rockwell Automation", Assert.Single(result.Devices).Vendor);
    }

    [Theory]
    [InlineData("not-a-mac,192.168.1.51,255.255.255.0,,,,,", "is not a MAC address")]
    [InlineData(",192.168.1.51,255.255.255.0,,,,,", "no MAC address")]
    [InlineData("FF:FF:FF:FF:FF:FF,192.168.1.51,255.255.255.0,,,,,", "broadcast address")]
    [InlineData("01:1D:9C:C7:B0:70,192.168.1.51,255.255.255.0,,,,,", "group bit")]
    [InlineData("00:00:00:00:00:00,192.168.1.51,255.255.255.0,,,,,", "all zeros")]
    [InlineData("00:1D:9C:C7:B0:70,192.168.1,255.255.255.0,,,,,", "is not an IPv4 address")]
    [InlineData("00:1D:9C:C7:B0:70,192.168.1.999,255.255.255.0,,,,,", "is not an IPv4 address")]
    [InlineData("00:1D:9C:C7:B0:70,192.168.1.51,255.255.0.255,,,,,", "hole in them")]
    [InlineData("00:1D:9C:C7:B0:70,192.168.1.51,not-a-mask,,,,,", "is not a subnet mask")]
    [InlineData("00:1D:9C:C7:B0:70,192.168.1.51,255.255.255.0,nonsense,,,,", "cannot be a gateway")]
    [InlineData("00:1D:9C:C7:B0:70,192.168.1.0,255.255.255.0,,,,,", "network address")]
    [InlineData("00:1D:9C:C7:B0:70,192.168.1.255,255.255.255.0,,,,,", "broadcast address of")]
    public void RefusesTheWholeFileAndSaysWhy(string row, string expected)
    {
        PlanValidationResult result = Validate(GoodRowWithMac("00:1D:9C:C7:B0:71") + row + "\r\n");

        Assert.False(result.IsValid);
        Assert.Empty(result.Devices);

        PlanProblem problem = Assert.Single(result.Problems);
        Assert.Contains(expected, problem.Message, StringComparison.Ordinal);

        // Line 1 is the header and line 2 is the good row, so the bad one is line 3.
        Assert.Equal(3, problem.LineNumber);
    }

    /// <summary>
    /// The check that is worth the most. Two devices on one address is the fault that is hardest to
    /// find on a panel - both work until they are powered at the same time - and it costs nothing
    /// to catch here.
    /// </summary>
    [Fact]
    public void RefusesTwoRowsPlanningOneAddress()
    {
        PlanValidationResult result = Validate(
            "00:1D:9C:C7:B0:70,192.168.1.51,255.255.255.0,,,,,\r\n"
            + "00:1D:9C:C7:B0:71,192.168.1.51,255.255.255.0,,,,,\r\n");

        PlanProblem problem = Assert.Single(result.Problems);
        Assert.Equal(3, problem.LineNumber);

        // It names the other row, because "duplicate address" without the other line number means
        // scrolling a hundred-row file looking for it.
        Assert.Contains("on line 2", problem.Message, StringComparison.Ordinal);
        Assert.Contains("00:1D:9C:C7:B0:70", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesTwoRowsWithOneMac()
    {
        PlanValidationResult result = Validate(
            "00:1D:9C:C7:B0:70,192.168.1.51,255.255.255.0,,,,,\r\n"
            + "00-1d-9c-c7-b0-70,192.168.1.52,255.255.255.0,,,,,\r\n");

        // Note the second row is punctuated differently. The plan is keyed on the parsed value, so
        // "AA:BB.." and "aa-bb.." are the same device - which is the whole reason MacAddress exists.
        PlanProblem problem = Assert.Single(result.Problems);
        Assert.Contains("already planned on line 2", problem.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every problem in one go. Fixing a file one refusal at a time, with a dialog between each,
    /// is the experience this pass exists to avoid.
    /// </summary>
    [Fact]
    public void ReportsEveryProblemInTheFileRatherThanTheFirst()
    {
        PlanValidationResult result = Validate(
            "not-a-mac,192.168.1.51,255.255.255.0,,,,,\r\n"
            + "00:1D:9C:C7:B0:71,192.168.1.300,255.255.255.0,,,,,\r\n"
            + "00:1D:9C:C7:B0:72,192.168.1.53,255.255.0.255,,,,,\r\n");

        Assert.Equal(3, result.Problems.Count);
        Assert.Equal(new[] { 2, 3, 4 }, result.Problems.Select(p => p.LineNumber));
        Assert.Contains("3 problems", result.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// With an adapter supplied, the plan is also checked against this machine. Handing the laptop's
    /// own address to a device leaves two things answering for it.
    /// </summary>
    [Fact]
    public void RefusesAnAddressThatIsTheAdaptersOwn()
    {
        PlanValidationResult result = ValidateAgainstAdapter(
            "00:1D:9C:C7:B0:70,192.168.1.10,255.255.255.0,,,,,\r\n");

        Assert.Contains("own address", Assert.Single(result.Problems).Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An address off the adapter's segment is a device the readback after Set static cannot reach,
    /// which reports Unverified for a write that worked perfectly. The message says so, because
    /// that is the failure it prevents.
    /// </summary>
    [Fact]
    public void RefusesAnAddressOffTheAdaptersSubnet()
    {
        PlanValidationResult result = ValidateAgainstAdapter(
            "00:1D:9C:C7:B0:70,10.0.0.51,255.255.255.0,,,,,\r\n");

        PlanProblem problem = Assert.Single(result.Problems);
        Assert.Contains("192.168.1.0/24", problem.Message, StringComparison.Ordinal);
        Assert.Contains("Unverified", problem.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same file, with no adapter supplied, imports. That is how a plan for a segment nobody is
    /// patched into yet gets typed up in advance - and it is the seam that keeps the three
    /// machine-specific checks from being a wall the tool cannot be talked past.
    /// </summary>
    [Fact]
    public void ImportsAPlanForAnotherSubnetWhenNoAdapterWasGiven()
    {
        PlanValidationResult result = Validate("00:1D:9C:C7:B0:70,10.0.0.51,255.255.255.0,,,,,\r\n");

        Assert.True(result.IsValid);
        Assert.Equal(IPAddress.Parse("10.0.0.51"), Assert.Single(result.Devices).PlannedIp);
    }

    /// <summary>
    /// A /26 is where a byte-wise comparison starts answering confidently and wrongly: .63 is the
    /// broadcast of 192.168.1.0/26 and .64 is a different segment entirely.
    /// </summary>
    [Fact]
    public void UnderstandsASubnetNarrowerThanAByte()
    {
        Assert.False(Validate("00:1D:9C:C7:B0:70,192.168.1.63,255.255.255.192,,,,,\r\n").IsValid);
        Assert.True(Validate("00:1D:9C:C7:B0:70,192.168.1.62,255.255.255.192,,,,,\r\n").IsValid);
    }

    /// <summary>
    /// A half-typed cell must not become an address nobody wrote. IPAddress.TryParse reads
    /// "192.168.1" as 192.168.0.1 and a bare "51" as 0.0.0.51, which would import silently.
    /// </summary>
    [Theory]
    [InlineData("192.168.1")]
    [InlineData("51")]
    [InlineData("192.168.1.51.7")]
    [InlineData("192.168.1.")]
    [InlineData(" 192.168.1.51 x")]
    public void RefusesShorthandThatWouldOtherwiseParseIntoAnAddressNobodyTyped(string address)
    {
        PlanValidationResult result = Validate($"00:1D:9C:C7:B0:70,{address},255.255.255.0,,,,,\r\n");

        Assert.False(result.IsValid);
        Assert.Empty(result.Devices);
    }

    /// <summary>Whitespace round an address is an accident; whitespace inside a note may not be.</summary>
    [Fact]
    public void TrimsTheAddressColumnsAndKeepsTheTextOnes()
    {
        PlanValidationResult result = Validate("  00:1D:9C:C7:B0:70 , 192.168.1.51 ,255.255.255.0,,,,,\r\n");

        Assert.True(result.IsValid);
        Assert.Equal(IPAddress.Parse("192.168.1.51"), Assert.Single(result.Devices).PlannedIp);
    }

    /// <summary>A cell holding only spaces is an empty cell, not a name. Same rule as the grid's.</summary>
    [Fact]
    public void TreatsABlankLabelAsNoLabel()
    {
        PlanValidationResult result = Validate("00:1D:9C:C7:B0:70,,,,   ,,,\r\n");

        Assert.Null(Assert.Single(result.Devices).HostName);
    }

    /// <summary>
    /// A valid row to sit above the bad one in the theory, so that every case proves the good row
    /// did not import either. Its address is deliberately not the one the bad rows use, or half the
    /// cases would report a duplicate address as well and the theory would be testing two things.
    /// </summary>
    private static string GoodRowWithMac(string mac) =>
        $"{mac},192.168.1.99,255.255.255.0,,,,,\r\n";

    private static PlanValidationResult Validate(string rows) =>
        PlanValidation.Validate(PlanCsv.Read(Header + rows));

    private static PlanValidationResult ValidateAgainstAdapter(string rows) =>
        PlanValidation.Validate(
            PlanCsv.Read(Header + rows),
            PlanImportContext.ForAdapter(Nics.Healthy()));
}
