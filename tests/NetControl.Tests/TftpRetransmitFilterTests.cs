using System.Net;
using NetControl.Core.Tftp;
using Xunit;

namespace NetControl.Tests;

/// <summary>
/// What counts as the same request arriving twice, when the protocol has no transaction id.
/// </summary>
public class TftpRetransmitFilterTests
{
    private static IPEndPoint Client(int port = 50000, string address = "192.168.1.51") =>
        new(IPAddress.Parse(address), port);

    [Fact]
    public void TheFirstRequestIsNeverARepeat()
    {
        var filter = new TftpRetransmitFilter(TimeSpan.FromSeconds(6), new TestTimeProvider());

        Assert.False(filter.IsRetransmit(Client(), "FROM00.IMG"));
    }

    [Fact]
    public void TheSameRequestFromTheSamePortIsARepeat()
    {
        var filter = new TftpRetransmitFilter(TimeSpan.FromSeconds(6), new TestTimeProvider());

        Assert.False(filter.IsRetransmit(Client(), "FROM00.IMG"));
        Assert.True(filter.IsRetransmit(Client(), "FROM00.IMG"));
    }

    [Fact]
    public void ADifferentSourcePortIsANewAttemptRatherThanARepeat()
    {
        // The source port is the client's transfer identifier. A controller that gave up and
        // started again comes back from a different one, and collapsing that into the first
        // attempt would hide the retry that matters - the second backup somebody tried.
        var filter = new TftpRetransmitFilter(TimeSpan.FromSeconds(6), new TestTimeProvider());

        Assert.False(filter.IsRetransmit(Client(50000), "FROM00.IMG"));
        Assert.False(filter.IsRetransmit(Client(50001), "FROM00.IMG"));
    }

    [Fact]
    public void ADifferentControllerIsNeverARepeatOfAnother()
    {
        var filter = new TftpRetransmitFilter(TimeSpan.FromSeconds(6), new TestTimeProvider());

        Assert.False(filter.IsRetransmit(Client(address: "192.168.1.51"), "FROM00.IMG"));
        Assert.False(filter.IsRetransmit(Client(address: "192.168.1.52"), "FROM00.IMG"));
    }

    [Fact]
    public void ADifferentFilenameFromOnePortIsNotARepeat()
    {
        var filter = new TftpRetransmitFilter(TimeSpan.FromSeconds(6), new TestTimeProvider());

        Assert.False(filter.IsRetransmit(Client(), "FROM00.IMG"));
        Assert.False(filter.IsRetransmit(Client(), "SRAM.IMG"));
    }

    [Fact]
    public void FilenamesAreComparedExactly()
    {
        // Compared the way it is reported. Whether two names differing only in case are the same
        // file is the server's business and its answer varies; this tool does not decide for it.
        var filter = new TftpRetransmitFilter(TimeSpan.FromSeconds(6), new TestTimeProvider());

        Assert.False(filter.IsRetransmit(Client(), "FROM00.IMG"));
        Assert.False(filter.IsRetransmit(Client(), "from00.img"));
    }

    [Fact]
    public void ARepeatOutsideTheWindowIsANewRequest()
    {
        var time = new TestTimeProvider();
        var filter = new TftpRetransmitFilter(TimeSpan.FromSeconds(6), time);

        Assert.False(filter.IsRetransmit(Client(), "FROM00.IMG"));

        time.Advance(TimeSpan.FromSeconds(30));

        Assert.False(filter.IsRetransmit(Client(), "FROM00.IMG"));
    }

    [Fact]
    public void PruningDoesNotForgetSomethingStillInsideItsWindow()
    {
        var filter = new TftpRetransmitFilter(TimeSpan.FromSeconds(6), new TestTimeProvider());

        filter.IsRetransmit(Client(50000), "FROM00.IMG");

        // Push well past the prune threshold with entries from other ports.
        for (int port = 51000; port < 52000; port++)
        {
            filter.IsRetransmit(Client(port), "OTHER.IMG");
        }

        Assert.True(filter.IsRetransmit(Client(50000), "FROM00.IMG"));
    }

    [Fact]
    public void ClearingForgetsEverything()
    {
        var filter = new TftpRetransmitFilter(TimeSpan.FromSeconds(6), new TestTimeProvider());

        Assert.False(filter.IsRetransmit(Client(), "FROM00.IMG"));
        filter.Clear();
        Assert.False(filter.IsRetransmit(Client(), "FROM00.IMG"));
    }
}
