using System.Buffers.Binary;
using System.Net;
using NetControl.Core;
using NetControl.Core.Analysis;
using NetControl.Core.Dhcp;
using Xunit;

namespace NetControl.Tests;

public sealed class TrafficMonitorTests
{
    private static readonly MacAddress Broadcast = MacAddress.Parse("FF:FF:FF:FF:FF:FF");
    private static readonly MacAddress Looper = MacAddress.Parse("00:00:BC:11:22:33");
    private static readonly MacAddress Quiet = MacAddress.Parse("00:0E:8C:44:55:66");

    [Fact]
    public void NothingIsCalledUntilAFewSecondsHaveBeenHeard()
    {
        var time = new TestTimeProvider();
        var monitor = new TrafficMonitor(timeProvider: time);

        for (int i = 0; i < 5000; i++)
        {
            monitor.Observe(Arp(Looper), time.GetUtcNow());
        }

        Assert.Empty(monitor.Snapshot().Findings);
    }

    [Fact]
    public void ASustainedBroadcastFloodIsAStormNamingTheTalker()
    {
        var time = new TestTimeProvider();
        var monitor = new TrafficMonitor(timeProvider: time);

        for (int second = 0; second < 12; second++)
        {
            for (int i = 0; i < 2000; i++)
            {
                monitor.Observe(Arp(Looper), time.GetUtcNow());
            }

            monitor.Observe(Arp(Quiet), time.GetUtcNow());
            time.Advance(TimeSpan.FromSeconds(1));
        }

        TrafficSnapshot snapshot = monitor.Snapshot();

        Assert.True(snapshot.BroadcastPerSecond > 1500, $"{snapshot.BroadcastPerSecond}");
        Assert.Equal(2001, snapshot.PeakBroadcastPerSecond);
        Assert.Equal(Looper, snapshot.TopTalkers[0].Mac);
        Assert.Equal(FindingSeverity.Error, snapshot.Findings[0].Severity);
        Assert.Contains(snapshot.Findings, f => f.Title.Contains("storm", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(snapshot.Findings, f => f.Title.Contains(Looper.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public void AQuietSegmentSaysSoAndSaysWhatWasChecked()
    {
        var time = new TestTimeProvider();
        var monitor = new TrafficMonitor(timeProvider: time);

        for (int second = 0; second < 5; second++)
        {
            monitor.Observe(Arp(Quiet), time.GetUtcNow());
            time.Advance(TimeSpan.FromSeconds(1));
        }

        NetworkFinding finding = Assert.Single(monitor.Snapshot().Findings);
        Assert.Equal(FindingSeverity.Ok, finding.Severity);
    }

    [Fact]
    public void AnotherDhcpServerIsNamedAndThisPcsOwnRepliesAreNot()
    {
        var time = new TestTimeProvider();
        var monitor = new TrafficMonitor(timeProvider: time);
        MacAddress me = MacAddress.Parse("00:1D:9C:C7:B0:70");
        MacAddress router = MacAddress.Parse("00:11:22:33:44:55");
        monitor.SetOwnMacs([me]);

        monitor.Observe(BootpReply(me, "192.168.1.10", "192.168.1.51"), time.GetUtcNow());
        monitor.Observe(BootpReply(router, "192.168.1.1", "192.168.1.100"), time.GetUtcNow());
        time.Advance(TimeSpan.FromSeconds(5));

        TrafficSnapshot snapshot = monitor.Snapshot();
        DhcpServerSighting server = Assert.Single(snapshot.OtherDhcpServers);
        Assert.Equal(IPAddress.Parse("192.168.1.1"), server.Address);
        Assert.Equal(IPAddress.Parse("192.168.1.100"), server.LastOffered);
        Assert.Contains(snapshot.Findings, f => f.Title.Contains("Another BOOTP/DHCP server", StringComparison.Ordinal));
    }

    [Fact]
    public void ATopologyChangeIsOneEventNotOnePerBpdu()
    {
        var time = new TestTimeProvider();
        var monitor = new TrafficMonitor(timeProvider: time);

        // One change: every bridge repeats the TC flag every two seconds for most of a minute.
        for (int i = 0; i < 15; i++)
        {
            monitor.Observe(Bpdu(topologyChange: true), time.GetUtcNow());
            time.Advance(TimeSpan.FromSeconds(2));
        }

        time.Advance(TimeSpan.FromMinutes(2));
        monitor.Observe(Bpdu(topologyChange: true), time.GetUtcNow());
        time.Advance(TimeSpan.FromSeconds(5));

        TrafficSnapshot snapshot = monitor.Snapshot();
        Assert.Equal(2, snapshot.TopologyChanges);
        Assert.Equal(16, snapshot.Stp);
        Assert.Contains(snapshot.Findings, f => f.Area == "Switches");
    }

    [Fact]
    public void HistoryIsBoundedAndSilentSecondsAreZero()
    {
        var time = new TestTimeProvider();
        var monitor = new TrafficMonitor(timeProvider: time);

        monitor.Observe(Arp(Quiet), time.GetUtcNow());
        time.Advance(TimeSpan.FromSeconds(500));

        TrafficSnapshot snapshot = monitor.Snapshot();
        Assert.Equal(TrafficMonitor.HistorySeconds, snapshot.History.Count);
        Assert.All(snapshot.History, n => Assert.Equal(0, n));
        Assert.Equal(1, snapshot.Broadcast);
    }

    private static byte[] Arp(MacAddress from)
    {
        var frame = new byte[42];
        Broadcast.CopyTo(frame);
        from.CopyTo(frame.AsSpan(6));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12), 0x0806);
        return frame;
    }

    private static byte[] BootpReply(MacAddress from, string server, string offered)
    {
        var reply = new BootpPacket
        {
            Op = BootpPacket.OpBootReply,
            HardwareType = 1,
            HardwareAddressLength = 6,
            TransactionId = 0x1234,
            ClientMac = MacAddress.Parse("00:00:BC:AA:BB:CC"),
            ServerIp = IPAddress.Parse(server),
            YourIp = IPAddress.Parse(offered),
        };

        byte[] bootp = reply.Serialize();
        var frame = new byte[14 + 20 + 8 + bootp.Length];
        Broadcast.CopyTo(frame);
        from.CopyTo(frame.AsSpan(6));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12), 0x0800);
        frame[14] = 0x45;
        frame[14 + 9] = 17;
        IPAddress.Parse(server).GetAddressBytes().CopyTo(frame, 14 + 12);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(34), 67);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(36), 68);
        bootp.CopyTo(frame, 42);
        return frame;
    }

    private static byte[] Bpdu(bool topologyChange)
    {
        var frame = new byte[14 + 3 + 35];
        MacAddress.Parse("01:80:C2:00:00:00").CopyTo(frame);
        Quiet.CopyTo(frame.AsSpan(6));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12), 38);
        frame[14] = 0x42;
        frame[15] = 0x42;
        frame[16] = 0x03;
        frame[17 + 3] = 0x00; // configuration BPDU
        frame[17 + 4] = topologyChange ? (byte)0x01 : (byte)0x00;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(17 + 5), 32768);
        Looper.CopyTo(frame.AsSpan(17 + 7));
        return frame;
    }
}
