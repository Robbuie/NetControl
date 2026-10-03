using NetControl.Core;
using NetControl.Core.Commissioning;
using Xunit;

namespace NetControl.Tests;

/// <summary>
/// What a Set static teaches about a device. Learned only from evidence the device gave, only ever
/// added, and every flag the enum has is named in the catalog the editor is built from.
/// </summary>
public class QuirkLearningTests
{
    [Fact]
    public void APinnedAddressIsLearnedFromNotSettable()
    {
        LearnedQuirks learned = QuirkLearning.Learn(Result(CommissionOutcome.NotSettable), DeviceQuirks.None);

        Assert.Equal(DeviceQuirks.HardwarePinnedAddress, learned.Flags);
        Assert.Contains("cannot be set over the network", Assert.Single(learned.Reasons), StringComparison.Ordinal);
    }

    [Fact]
    public void AHeldConfigurationTeachesTheResetQuirk()
    {
        LearnedQuirks learned = QuirkLearning.Learn(
            Result(CommissionOutcome.Unverified) with { ResetPending = true, WroteToDevice = true },
            DeviceQuirks.None);

        Assert.Equal(DeviceQuirks.RequiresResetToApply, learned.Flags);
    }

    [Fact]
    public void AMismatchAfterAWriteIsALie()
    {
        LearnedQuirks learned = QuirkLearning.Learn(
            Result(CommissionOutcome.Mismatch) with { WroteToDevice = true },
            DeviceQuirks.None);

        Assert.Equal(DeviceQuirks.LiesAboutWriteSuccess, learned.Flags);
    }

    /// <summary>A timeout proves nothing about the device - the laptop may be on the wrong VLAN.</summary>
    [Fact]
    public void NeverLearnsSlownessOrAnythingFromUnreachable()
    {
        Assert.False(QuirkLearning.Learn(Result(CommissionOutcome.Unreachable), DeviceQuirks.None).Any);
        Assert.False(QuirkLearning.Learn(Result(CommissionOutcome.Verified), DeviceQuirks.None).Any);
    }

    [Fact]
    public void DoesNotRelearnWhatTheDeviceAlreadyCarries()
    {
        LearnedQuirks learned = QuirkLearning.Learn(
            Result(CommissionOutcome.NotSettable),
            DeviceQuirks.HardwarePinnedAddress | DeviceQuirks.SlowResponses);

        Assert.False(learned.Any);
        Assert.Empty(learned.Reasons);
    }

    [Fact]
    public void EveryFlagIsNamedOnceInTheCatalog()
    {
        DeviceQuirks[] flags = [.. Enum.GetValues<DeviceQuirks>().Where(f => f != DeviceQuirks.None)];

        Assert.Equal(flags.Length, DeviceQuirkCatalog.All.Count);
        Assert.All(flags, f => Assert.NotNull(DeviceQuirkCatalog.For(f)));
        Assert.Equal(
            DeviceQuirkCatalog.All.Count,
            DeviceQuirkCatalog.All.Select(q => q.Short).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ShortTextListsTheFlagsInCatalogOrder()
    {
        Assert.Equal(string.Empty, DeviceQuirkCatalog.ShortText(DeviceQuirks.None));
        Assert.Equal(
            "reset, slow",
            DeviceQuirkCatalog.ShortText(DeviceQuirks.SlowResponses | DeviceQuirks.RequiresResetToApply));
    }

    private static CommissionResult Result(CommissionOutcome outcome) => new()
    {
        Outcome = outcome,
        Message = outcome.ToString(),
    };
}
