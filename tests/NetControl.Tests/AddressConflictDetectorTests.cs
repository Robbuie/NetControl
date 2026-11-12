using System.Net;
using NetControl.Core;
using NetControl.Core.Analysis;
using NetControl.Core.Interfaces;
using Xunit;

namespace NetControl.Tests;

public sealed class AddressConflictDetectorTests
{
    private static readonly IPAddress Address = IPAddress.Parse("192.168.1.51");
    private static readonly MacAddress Drive = MacAddress.Parse("00:00:BC:11:22:33");
    private static readonly MacAddress Hmi = MacAddress.Parse("00:0E:8C:44:55:66");

    [Fact]
    public void OneMacOnOneAddressIsNotAConflict()
    {
        var detector = new AddressConflictDetector(timeProvider: new TestTimeProvider());

        detector.Observe(Address, Drive, AddressObservationSource.ArpFrame);
        detector.Observe(Address, Drive, AddressObservationSource.ArpTable);

        Assert.Empty(detector.Conflicts());
    }

    [Fact]
    public void TwoMacsOnOneAddressAreAConflictAndRaiseOnce()
    {
        var time = new TestTimeProvider();
        var detector = new AddressConflictDetector(timeProvider: time);
        var raised = new List<AddressConflict>();
        detector.ConflictDetected += (_, c) => raised.Add(c);

        detector.Observe(Address, Drive, AddressObservationSource.ArpFrame);
        time.Advance(TimeSpan.FromSeconds(5));
        detector.Observe(Address, Hmi, AddressObservationSource.ArpFrame);
        time.Advance(TimeSpan.FromSeconds(5));
        detector.Observe(Address, Drive, AddressObservationSource.ArpFrame);

        AddressConflict conflict = Assert.Single(detector.Conflicts());
        Assert.Equal([Drive, Hmi], conflict.Claimants.Select(c => c.Mac));
        Assert.False(conflict.LooksLikeAMove);
        Assert.Equal(FindingSeverity.Error, conflict.ToFinding().Severity);
        Assert.Single(raised);
    }

    [Fact]
    public void AnAddressThatChangedHandsCleanlyReadsAsAMove()
    {
        var time = new TestTimeProvider();
        var detector = new AddressConflictDetector(timeProvider: time);

        detector.Observe(Address, Drive, AddressObservationSource.ArpTable);
        time.Advance(TimeSpan.FromMinutes(1));
        detector.Observe(Address, Hmi, AddressObservationSource.ArpTable);

        AddressConflict conflict = Assert.Single(detector.Conflicts());
        Assert.True(conflict.LooksLikeAMove);
        Assert.Equal(FindingSeverity.Warn, conflict.ToFinding().Severity);
    }

    [Fact]
    public void AClaimOutsideTheWindowIsForgotten()
    {
        var time = new TestTimeProvider();
        var detector = new AddressConflictDetector(TimeSpan.FromMinutes(1), time);

        detector.Observe(Address, Drive, AddressObservationSource.ArpFrame);
        time.Advance(TimeSpan.FromMinutes(5));
        detector.Observe(Address, Hmi, AddressObservationSource.ArpFrame);

        Assert.Empty(detector.Conflicts());
    }

    [Fact]
    public void AnythingElseClaimingThisPcsAddressIsAConflictOnItsOwn()
    {
        var detector = new AddressConflictDetector(timeProvider: new TestTimeProvider());
        IPAddress mine = IPAddress.Parse("192.168.1.10");
        MacAddress myMac = MacAddress.Parse("00:1D:9C:C7:B0:70");
        detector.SetOwnAddresses([(mine, myMac)]);

        detector.Observe(mine, myMac, AddressObservationSource.ArpFrame);
        Assert.Empty(detector.Conflicts());

        detector.Observe(mine, Hmi, AddressObservationSource.Gratuitous);

        AddressConflict conflict = Assert.Single(detector.Conflicts());
        Assert.True(conflict.IsOwnAddress);
        Assert.Equal(Hmi, Assert.Single(conflict.Claimants).Mac);
    }

    [Fact]
    public void AProbeForAnAddressSomebodyHoldsIsReported()
    {
        var detector = new AddressConflictDetector(timeProvider: new TestTimeProvider());

        detector.Observe(Address, Drive, AddressObservationSource.ArpFrame);
        detector.Observe(Address, Hmi, AddressObservationSource.Probe);

        AddressConflict conflict = Assert.Single(detector.Conflicts());
        Assert.Equal(Hmi, Assert.Single(conflict.ProbedBy));
    }

    [Fact]
    public void ArpEntriesFromAnotherAdapterOrInvalidOnesAreIgnored()
    {
        var detector = new AddressConflictDetector(timeProvider: new TestTimeProvider());

        detector.ObserveArpTable(
            [
                new ArpEntry(Address, Drive, 12, ArpEntryType.Dynamic),
                new ArpEntry(Address, Hmi, 7, ArpEntryType.Dynamic),
                new ArpEntry(Address, MacAddress.Parse("00:00:BC:99:99:99"), 12, ArpEntryType.Invalid),
            ],
            interfaceIndex: 12);

        Assert.Empty(detector.Conflicts());
        Assert.Equal(1, detector.Observations);
    }

    [Fact]
    public void MulticastAndUnspecifiedAddressesAreNeverTracked()
    {
        var detector = new AddressConflictDetector(timeProvider: new TestTimeProvider());

        detector.Observe(IPAddress.Parse("239.192.1.1"), Drive, AddressObservationSource.ArpFrame);
        detector.Observe(IPAddress.Any, Drive, AddressObservationSource.ArpFrame);

        Assert.Equal(0, detector.AddressesTracked);
    }
}
