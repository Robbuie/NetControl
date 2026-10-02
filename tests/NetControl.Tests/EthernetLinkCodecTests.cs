using NetControl.Core.Cip;
using NetControl.Core.DeviceHealth;
using NetControl.Core.Enip;
using Xunit;

namespace NetControl.Tests;

/// <summary>
/// The Ethernet Link counters and the status words, decoded from literal bytes.
///
/// <para>Literal bytes on purpose. The simulator was extended from the same reading of the spec as
/// the client, so a shared misreading of the counter order would pass every test that goes through
/// it. These pin the offsets - FCS errors are the second UDINT, late collisions the seventh - so a
/// "fix" that shuffles them fails here first.</para>
/// </summary>
public class EthernetLinkCodecTests
{
    [Fact]
    public void ReadsTheMediaCountersInTheSpecsOrderAndLittleEndian()
    {
        // Twelve UDINTs: each holds its own position number, plus 0x100 to prove the byte order.
        byte[] bytes = Udints(12, n => 0x100u + n);

        MediaCounters media = MediaCounters.Parse(bytes);

        Assert.Equal(0x100u, media.AlignmentErrors);
        Assert.Equal(0x101u, media.FcsErrors);
        Assert.Equal(0x102u, media.SingleCollisions);
        Assert.Equal(0x103u, media.MultipleCollisions);
        Assert.Equal(0x106u, media.LateCollisions);
        Assert.Equal(0x107u, media.ExcessiveCollisions);
        Assert.Equal(0x10Au, media.FrameTooLong);
        Assert.Equal(0x10Bu, media.MacReceiveErrors);
    }

    [Fact]
    public void ReadsTheInterfaceCountersInTheSpecsOrder()
    {
        InterfaceCounters counters = InterfaceCounters.Parse(Udints(11, n => n * 10));

        Assert.Equal(0u, counters.InOctets);
        Assert.Equal(20u, counters.InNonUnicastPackets);
        Assert.Equal(30u, counters.InDiscards);
        Assert.Equal(40u, counters.InErrors);
        Assert.Equal(100u, counters.OutErrors);
    }

    [Fact]
    public void RefusesCountersThatAreTooShortRatherThanReadingPastThem()
    {
        Assert.Throws<EnipException>(() => MediaCounters.Parse(new byte[44]));
        Assert.Throws<EnipException>(() => InterfaceCounters.Parse(new byte[40]));
    }

    /// <summary>Bits 2-4 are a number, not three flags - which is why this is not a [Flags] enum.</summary>
    [Theory]
    [InlineData(0x0Fu, true, true, NegotiationStatus.Negotiated)]
    [InlineData(0x09u, true, false, NegotiationStatus.DuplexDefaulted)]
    [InlineData(0x13u, true, true, NegotiationStatus.Forced)]
    [InlineData(0x05u, true, false, NegotiationStatus.FailedUsingDefaults)]
    [InlineData(0x00u, false, false, NegotiationStatus.InProgress)]
    public void DecodesTheInterfaceFlags(uint raw, bool link, bool full, NegotiationStatus negotiation)
    {
        var flags = new LinkFlags(raw);

        Assert.Equal(link, flags.LinkActive);
        Assert.Equal(full, flags.FullDuplex);
        Assert.Equal(negotiation, flags.Negotiation);
    }

    [Fact]
    public void DecodesTheHardwareFaultAndResetPendingBits()
    {
        var flags = new LinkFlags(0x0F | 0x20 | 0x40);

        Assert.True(flags.ManualSettingRequiresReset);
        Assert.True(flags.LocalHardwareFault);
    }

    [Fact]
    public void DecodesTheIdentityStatusWord()
    {
        // Owned, extended status 2 (an I/O connection faulted), minor recoverable fault.
        var status = new IdentityStatus(0x0001 | (2 << 4) | 0x0100);

        Assert.True(status.Owned);
        Assert.Equal(2, status.ExtendedStatus);
        Assert.True(status.MinorRecoverableFault);
        Assert.False(status.HasMajorFault);
        Assert.Contains("I/O connection faulted", status.ExtendedStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void DecodesTheTcpIpStatusBits()
    {
        var status = new TcpIpStatus(0x02 | 0x20 | 0x40);

        Assert.Equal(2, status.ConfigurationStatus);
        Assert.True(status.ConfigurationPending);
        Assert.True(status.ConflictDetected);
        Assert.False(status.ConflictFault);
        Assert.Contains("hardware switches", status.ToString(), StringComparison.Ordinal);
    }

    private static byte[] Udints(int count, Func<uint, uint> value)
    {
        var bytes = new byte[count * 4];

        for (int i = 0; i < count; i++)
        {
            uint v = value((uint)i);
            bytes[i * 4] = (byte)v;
            bytes[(i * 4) + 1] = (byte)(v >> 8);
            bytes[(i * 4) + 2] = (byte)(v >> 16);
            bytes[(i * 4) + 3] = (byte)(v >> 24);
        }

        return bytes;
    }
}
