using NetControl.Core.Safety;
using Xunit;

namespace NetControl.Tests;

/// <summary>
/// The one gate every transmission passes. Each test builds its own guard: the shared one is used by
/// every engine in every other test, and a test that flipped it to listen-only would break them.
/// </summary>
public sealed class NetworkGuardTests
{
    [Fact]
    public void ListenOnlyBlocksAndRecordsTheBlockedTransmission()
    {
        var guard = new NetworkGuard { ListenOnly = true };

        TransmitBlockedException ex = Assert.Throws<TransmitBlockedException>(
            () => guard.Acquire(TransmitKind.DhcpReply, "255.255.255.255:68", "reply to 00:1D:9C:C7:B0:70"));

        Assert.Contains("Listen-only", ex.Message, StringComparison.Ordinal);
        Assert.NotNull(ex.Remediation);

        SentPacket entry = Assert.Single(guard.Ledger.Snapshot());
        Assert.True(entry.Blocked);
        Assert.Equal(TransmitKind.DhcpReply, entry.Kind);
        Assert.Equal(1, guard.Ledger.TotalBlocked);
        Assert.Equal(0, guard.Ledger.TotalSent);
    }

    [Fact]
    public async Task ListenOnlyBlocksTheAsyncPathToo()
    {
        var guard = new NetworkGuard { ListenOnly = true };

        await Assert.ThrowsAsync<TransmitBlockedException>(
            async () => await guard.AcquireAsync(TransmitKind.Ping, "10.0.0.5", "ping"));
    }

    [Fact]
    public void AnAllowedTransmissionIsRecordedAsSent()
    {
        var guard = new NetworkGuard();

        guard.Acquire(TransmitKind.ListIdentity, "255.255.255.255:44818", "Scan", 24);

        SentPacket entry = Assert.Single(guard.Ledger.Snapshot());
        Assert.False(entry.Blocked);
        Assert.Equal(24, entry.Bytes);
        Assert.Equal((TransmitKind.ListIdentity, 1L, 0L), Assert.Single(guard.Ledger.Totals()));
    }

    [Fact]
    public void TheListenOnlyCheckAloneRecordsNothingWhenAllowed()
    {
        var guard = new NetworkGuard();

        guard.ThrowIfListenOnly(TransmitKind.Tftp, "10.0.0.5:2000", "block 7");

        Assert.Equal(0, guard.Ledger.Count);
    }

    [Fact]
    public async Task TheRateCapMakesACallerWaitRatherThanRefusingIt()
    {
        var guard = new NetworkGuard { MaxPacketsPerSecond = NetworkGuard.MinimumMaxPacketsPerSecond };
        var clock = System.Diagnostics.Stopwatch.StartNew();

        // The bucket holds one second's worth; the next five have to wait for refill at 10/s.
        for (int i = 0; i < NetworkGuard.MinimumMaxPacketsPerSecond + 5; i++)
        {
            await guard.AcquireAsync(TransmitKind.Ping, "10.0.0.5", "ping");
        }

        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(350), $"took {clock.Elapsed}");
        Assert.Equal(NetworkGuard.MinimumMaxPacketsPerSecond + 5, guard.Ledger.TotalSent);
    }

    [Fact]
    public void ACapBelowTheMinimumIsRaisedAndZeroMeansNone()
    {
        var guard = new NetworkGuard { MaxPacketsPerSecond = 1 };
        Assert.Equal(NetworkGuard.MinimumMaxPacketsPerSecond, guard.MaxPacketsPerSecond);

        guard.MaxPacketsPerSecond = 0;
        Assert.Equal(0, guard.MaxPacketsPerSecond);

        // No cap: a thousand in a row with no waiting at all.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++)
        {
            guard.Acquire(TransmitKind.Ping, "10.0.0.5", "ping");
        }

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void ChangingTheModeRaisesChangedOnce()
    {
        var guard = new NetworkGuard();
        int changed = 0;
        guard.Changed += (_, _) => changed++;

        guard.ListenOnly = true;
        guard.ListenOnly = true;
        guard.ListenOnly = false;

        Assert.Equal(2, changed);
    }

    [Fact]
    public void TheLedgerKeepsTheNewestAndCountsEverything()
    {
        var ledger = new SentPacketLedger(capacity: 3);

        for (int i = 0; i < 5; i++)
        {
            ledger.Add(new SentPacket(DateTimeOffset.UnixEpoch.AddSeconds(i), TransmitKind.Ping, $"10.0.0.{i}", "ping", 0, false));
        }

        IReadOnlyList<SentPacket> kept = ledger.Snapshot();
        Assert.Equal(["10.0.0.2", "10.0.0.3", "10.0.0.4"], kept.Select(p => p.Destination));
        Assert.Equal(5, ledger.TotalSent);
    }

    [Fact]
    public void TheLedgerQuotesCsvFieldsThatNeedIt()
    {
        var ledger = new SentPacketLedger();
        ledger.Add(new SentPacket(DateTimeOffset.UnixEpoch, TransmitKind.Tftp, "10.0.0.5:69", "TFTP write \"image, 1\"", 0, true));

        string csv = ledger.ToCsv();

        Assert.Contains("\"TFTP write \"\"image, 1\"\"\"", csv, StringComparison.Ordinal);
        Assert.Contains(",yes\r\n", csv, StringComparison.Ordinal);
    }
}
