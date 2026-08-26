using System.Net;
using NetControl.App.ViewModels;
using NetControl.Core;
using NetControl.Core.Persistence;
using Xunit;

namespace NetControl.Tests;

/// <summary>
/// One row of the plan, and the distinction the whole grid rests on: a row that cannot be stored
/// versus a row that stores fine and will not be served.
///
/// A MAC copied off a label at 7am with the address still to be decided is a normal row, not a
/// mistake. Colouring it the same as a typo is how a grid full of amber gets ignored, and a grid
/// people ignore is one that lets "3 of 14 planned devices will not be served" reach the panel.
/// </summary>
public class DeviceRowViewModelTests
{
    private static readonly MacAddress Drive = MacAddress.Parse("00:1D:9C:C7:B0:70");

    [Fact]
    public void ABlankRowIsNotAnError()
    {
        DeviceRowViewModel row = DeviceRowViewModel.Blank();

        Assert.True(row.IsBlank);
        Assert.False(row.HasProblem);
        Assert.False(row.TryBuild(out _));
    }

    [Fact]
    public void AMacAndNothingElseStoresAndSaysItCannotBeServed()
    {
        DeviceRowViewModel row = DeviceRowViewModel.Blank();
        row.MacText = "00:1D:9C:C7:B0:70";

        Assert.False(row.HasProblem);
        Assert.False(row.IsServable);
        Assert.NotNull(row.ServeNote);
        Assert.True(row.TryBuild(out DeviceRecord? built));
        Assert.Equal(Drive, built!.Mac);
    }

    /// <summary>
    /// The messages come from <see cref="DeviceRecord.TryToAssignment"/> rather than being written
    /// again here. Two sets of wording for the same finding is how a row and the plan summary end
    /// up disagreeing about whether something is wrong.
    /// </summary>
    [Fact]
    public void ReusesThePlannersOwnWordingForARowThatWillNotBeServed()
    {
        DeviceRowViewModel row = DeviceRowViewModel.Blank();
        row.MacText = "00:1D:9C:C7:B0:70";
        row.IpText = "192.168.1.51";

        var record = new DeviceRecord
        {
            Mac = Drive,
            PlannedIp = IPAddress.Parse("192.168.1.51"),
        };
        record.TryToAssignment(out _, out string? expected);

        Assert.Equal(expected, row.ServeNote);
    }

    [Fact]
    public void ACompleteRowIsServable()
    {
        DeviceRowViewModel row = Complete();

        Assert.False(row.HasProblem);
        Assert.True(row.IsServable);
        Assert.Null(row.ServeNote);
    }

    /// <summary>
    /// A mask with a hole in it is the classic plan typo, and it is refused by
    /// <c>DeviceAssignment</c> rather than by anything written here.
    /// </summary>
    [Fact]
    public void ANonContiguousMaskIsCaughtBeforeItReachesAPanel()
    {
        DeviceRowViewModel row = Complete();
        row.MaskText = "255.255.0.255";

        Assert.False(row.IsServable);
        Assert.NotNull(row.ServeNote);
    }

    [Theory]
    [InlineData("00:1D:9C:C7:B0")]
    [InlineData("not a mac")]
    [InlineData("00:1D:9C:C7:B0:70:AA")]
    public void AMacThatDoesNotParseBlocksTheWrite(string typed)
    {
        DeviceRowViewModel row = DeviceRowViewModel.Blank();
        row.MacText = typed;

        Assert.True(row.HasProblem);
        Assert.False(row.TryBuild(out _));
    }

    /// <summary>
    /// Broadcast and multicast parse perfectly well and identify no single device. Serve mode
    /// answers exactly the MACs in the plan, so one of these in the table is a rogue reply waiting
    /// to happen.
    /// </summary>
    [Theory]
    [InlineData("FF:FF:FF:FF:FF:FF")]
    [InlineData("01:00:5E:00:00:01")]
    public void AMacThatCannotIdentifyOneDeviceBlocksTheWrite(string typed)
    {
        DeviceRowViewModel row = DeviceRowViewModel.Blank();
        row.MacText = typed;

        Assert.True(row.HasProblem);
        Assert.False(row.TryBuild(out _));
    }

    /// <summary>
    /// Half of "192.168.1." is not an address, and the row has to keep it while it is being typed.
    /// Binding a typed property would have WPF swallow the text and paint a box with no sentence.
    /// </summary>
    [Fact]
    public void KeepsWhatWasTypedWhileItIsStillWrong()
    {
        DeviceRowViewModel row = Complete();
        row.IpText = "192.168.1.999";

        Assert.True(row.HasProblem);
        Assert.Contains("192.168.1.999", row.Problem!, StringComparison.Ordinal);
        Assert.Equal("192.168.1.999", row.IpText);
        Assert.False(row.TryBuild(out _));
    }

    /// <summary>BOOTP has nowhere to put a v6 address, so one is a problem rather than a curiosity.</summary>
    [Fact]
    public void RefusesAnIPv6Address()
    {
        DeviceRowViewModel row = Complete();
        row.IpText = "fe80::1";

        Assert.True(row.HasProblem);
    }

    [Fact]
    public void AClearedAddressCellIsAPlanInProgressNotAMistake()
    {
        DeviceRowViewModel row = Complete();
        row.GatewayText = "   ";

        Assert.False(row.HasProblem);
        Assert.True(row.TryBuild(out DeviceRecord? built));
        Assert.Null(built!.PlannedGateway);
    }

    /// <summary>
    /// Loading a row must not look like editing it, or the grid would write every row straight
    /// back to the file it just read them from.
    /// </summary>
    [Fact]
    public void LoadingARowDoesNotCountAsAnEdit()
    {
        int edits = 0;

        DeviceRowViewModel row = DeviceRowViewModel.FromRecord(new DeviceRecord
        {
            Id = 7,
            Mac = Drive,
            PlannedIp = IPAddress.Parse("192.168.1.51"),
            PlannedMask = IPAddress.Parse("255.255.255.0"),
            Vendor = "Rockwell Automation",
        });

        row.Edited += (_, _) => edits++;

        Assert.Equal(0, edits);
        Assert.Equal(7, row.Id);
        Assert.Equal("00:1D:9C:C7:B0:70", row.MacText);
        Assert.Equal("Rockwell Automation", row.Vendor);
        Assert.True(row.IsServable);
    }

    [Fact]
    public void QuirksSurviveAnEditThatKnowsNothingAboutThem()
    {
        DeviceRowViewModel row = DeviceRowViewModel.FromRecord(new DeviceRecord
        {
            Id = 3,
            Mac = Drive,
            Quirks = DeviceQuirks.RequiresResetToApply,
        });

        row.Notes = "found on the bench";

        Assert.True(row.TryBuild(out DeviceRecord? built));
        Assert.Equal(DeviceQuirks.RequiresResetToApply, built!.Quirks);
    }

    private static DeviceRowViewModel Complete()
    {
        DeviceRowViewModel row = DeviceRowViewModel.Blank();
        row.MacText = "00:1D:9C:C7:B0:70";
        row.IpText = "192.168.1.51";
        row.MaskText = "255.255.255.0";
        row.GatewayText = "192.168.1.1";
        return row;
    }
}
