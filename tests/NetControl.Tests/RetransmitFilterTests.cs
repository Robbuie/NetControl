using NetControl.Core;
using NetControl.Core.Dhcp;
using Xunit;

namespace NetControl.Tests;

public class RetransmitFilterTests
{
    private static readonly MacAddress Mac = MacAddress.Parse(Frames.DeviceMac);
    private static readonly MacAddress Other = MacAddress.Parse("00:1D:9C:C7:B0:70");

    [Fact]
    public void TheFirstSightingIsNeverARetransmit()
    {
        var filter = new RetransmitFilter(TimeSpan.FromSeconds(3), new TestTimeProvider());

        Assert.False(filter.IsRetransmit(0xFC5D1465, Mac));
    }

    [Fact]
    public void TheSameRequestInsideTheWindowIsARetransmit()
    {
        var time = new TestTimeProvider();
        var filter = new RetransmitFilter(TimeSpan.FromSeconds(3), time);

        filter.IsRetransmit(0xFC5D1465, Mac);
        time.Advance(TimeSpan.FromMilliseconds(400));

        Assert.True(filter.IsRetransmit(0xFC5D1465, Mac));
    }

    [Fact]
    public void ARepeatAfterTheWindowIsANewRequest()
    {
        var time = new TestTimeProvider();
        var filter = new RetransmitFilter(TimeSpan.FromSeconds(3), time);

        filter.IsRetransmit(0xFC5D1465, Mac);
        time.Advance(TimeSpan.FromSeconds(4));

        Assert.False(filter.IsRetransmit(0xFC5D1465, Mac));
    }

    [Fact]
    public void TwoDevicesSharingATransactionIdAreNotEachOthersRetransmits()
    {
        var filter = new RetransmitFilter(TimeSpan.FromSeconds(3), new TestTimeProvider());

        filter.IsRetransmit(0xFC5D1465, Mac);

        Assert.False(filter.IsRetransmit(0xFC5D1465, Other));
    }

    [Fact]
    public void PruningDoesNotForgetSomethingStillInsideItsWindow()
    {
        var time = new TestTimeProvider();
        var filter = new RetransmitFilter(TimeSpan.FromSeconds(3), time);

        filter.IsRetransmit(1, Mac);

        // Push well past the prune threshold with stale entries.
        for (uint xid = 1000; xid < 2000; xid++)
        {
            filter.IsRetransmit(xid, Other);
        }

        Assert.True(filter.IsRetransmit(1, Mac));
    }
}
