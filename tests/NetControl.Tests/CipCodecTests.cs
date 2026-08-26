using System.Net;
using NetControl.Core.Cip;
using NetControl.Core.Enip;
using Xunit;

namespace NetControl.Tests;

/// <summary>
/// The CIP encoding, byte by byte.
///
/// <para>The commissioner tests prove the sequence end to end, but they run against a simulator
/// that was written from the same specification, so a shared misreading would pass both sides.
/// These assert against literal bytes instead - the ones a device on a wire will actually see.</para>
/// </summary>
public class CipCodecTests
{
    /// <summary>
    /// <b>The most expensive mistake available in this protocol.</b> Addresses inside CIP
    /// attributes are little-endian UDINTs, the reverse of <see cref="IPAddress.GetAddressBytes"/>.
    /// Writing 192.168.1.51 as 51.1.168.192 is accepted by the device, which then vanishes off the
    /// network at an address nobody is looking for.
    /// </summary>
    [Fact]
    public void WritesAnAddressLittleEndianWhichIsTheReverseOfTheWire()
    {
        var config = new InterfaceConfig
        {
            Ip = IPAddress.Parse("192.168.1.51"),
            Mask = IPAddress.Parse("255.255.255.0"),
        };

        byte[] bytes = config.Serialize();

        Assert.Equal(new byte[] { 51, 1, 168, 192 }, bytes[..4]);
        Assert.Equal(new byte[] { 0, 255, 255, 255 }, bytes[4..8]);

        // And the reverse of what a socket would hand you, which is the whole point.
        Assert.Equal(new byte[] { 192, 168, 1, 51 }, config.Ip.GetAddressBytes());
    }

    [Fact]
    public void RoundTripsAnInterfaceConfiguration()
    {
        var original = new InterfaceConfig
        {
            Ip = IPAddress.Parse("10.20.30.40"),
            Mask = IPAddress.Parse("255.255.0.0"),
            Gateway = IPAddress.Parse("10.20.0.1"),
            NameServer1 = IPAddress.Parse("8.8.8.8"),
            DomainName = "plant.local",
        };

        InterfaceConfig read = InterfaceConfig.Parse(original.Serialize());

        Assert.Equal(original.Ip, read.Ip);
        Assert.Equal(original.Mask, read.Mask);
        Assert.Equal(original.Gateway, read.Gateway);
        Assert.Equal(original.NameServer1, read.NameServer1);
        Assert.Equal("plant.local", read.DomainName);
    }

    /// <summary>A CIP STRING is padded to an even overall length; an odd domain name is where that shows.</summary>
    [Fact]
    public void PadsAnOddLengthDomainName()
    {
        var config = new InterfaceConfig
        {
            Ip = IPAddress.Loopback,
            Mask = IPAddress.Parse("255.0.0.0"),
            DomainName = "abc",
        };

        byte[] bytes = config.Serialize();

        Assert.Equal(0, bytes.Length % 2);
        Assert.Equal("abc", InterfaceConfig.Parse(bytes).DomainName);
    }

    /// <summary>
    /// A device with no gateway holds 0.0.0.0, and a plan with no gateway is not a mismatch with
    /// one. Comparing it anyway would fail every device that never had one.
    /// </summary>
    [Fact]
    public void IgnoresTheGatewayWhenThePlanDoesNotNameOne()
    {
        var actual = new InterfaceConfig
        {
            Ip = IPAddress.Parse("192.168.1.51"),
            Mask = IPAddress.Parse("255.255.255.0"),
            Gateway = IPAddress.Parse("192.168.1.1"),
        };

        Assert.True(actual.Matches(actual.Ip, actual.Mask, gateway: null));
        Assert.False(actual.Matches(actual.Ip, actual.Mask, IPAddress.Parse("192.168.1.254")));
    }

    /// <summary>
    /// service, EPATH length in 16-bit words, then the class, instance and attribute segments.
    /// Class 0xF5 attribute 5 is the write that carries the address.
    /// </summary>
    [Fact]
    public void EncodesTheEpathForTheTcpIpInterfaceObject()
    {
        byte[] bytes = CipRequest
            .SetAttribute(CipClass.TcpIpInterface, 5, new byte[] { 1, 2, 3, 4 })
            .Serialize();

        Assert.Equal(CipService.SetAttributeSingle, bytes[0]);
        Assert.Equal(3, bytes[1]);                                  // three 16-bit words of path
        Assert.Equal(new byte[] { 0x20, 0xF5 }, bytes[2..4]);       // class 0xF5
        Assert.Equal(new byte[] { 0x24, 0x01 }, bytes[4..6]);       // instance 1
        Assert.Equal(new byte[] { 0x30, 0x05 }, bytes[6..8]);       // attribute 5
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, bytes[8..]);
    }

    /// <summary>A service acting on the whole object carries no attribute segment.</summary>
    [Fact]
    public void LeavesTheAttributeSegmentOutWhenThereIsNoAttribute()
    {
        byte[] bytes = new CipRequest { Service = CipService.Reset, Class = CipClass.Identity }.Serialize();

        Assert.Equal(CipService.Reset, bytes[0]);
        Assert.Equal(2, bytes[1]);
        Assert.Equal(new byte[] { 0x20, 0x01, 0x24, 0x01 }, bytes[2..]);
    }

    /// <summary>
    /// A class id that will not fit in a byte takes the 16-bit form, which needs a pad byte after
    /// the segment type. Leaving the pad out produces a path the device rejects with a status that
    /// says nothing about why.
    /// </summary>
    [Fact]
    public void UsesTheSixteenBitSegmentFormWithItsPadForALargeClassId()
    {
        byte[] bytes = CipRequest.GetAttribute(0x0301, 1).Serialize();

        Assert.Equal(new byte[] { 0x21, 0x00, 0x01, 0x03 }, bytes[2..6]);
    }

    [Fact]
    public void ParsesASuccessfulReplyWithItsData()
    {
        CipResponse response = CipResponse.Parse([0x8E, 0x00, 0x00, 0x00, 0xDE, 0xAD]);

        Assert.True(response.IsSuccess);
        Assert.Equal(new byte[] { 0xDE, 0xAD }, response.Data);
    }

    /// <summary>
    /// A refusal is not an exception. The device understood and said no, and which no it was
    /// decides what happens next - so it comes back as a status with words on it.
    /// </summary>
    [Fact]
    public void ParsesARefusalAsAStatusRatherThanThrowing()
    {
        CipResponse response = CipResponse.Parse([0x90, 0x00, CipGeneralStatus.AttributeNotSettable, 0x00]);

        Assert.False(response.IsSuccess);
        Assert.Contains("refusing the write", response.StatusText, StringComparison.Ordinal);
        Assert.Contains("switches", response.StatusText, StringComparison.Ordinal);
    }

    /// <summary>The status a device sends when attribute 5 is written while it is still in BOOTP mode.</summary>
    [Fact]
    public void ExplainsAnObjectStateConflictAsAModeProblem()
    {
        string text = CipGeneralStatus.Describe(CipGeneralStatus.ObjectStateConflict);

        Assert.Contains("BOOTP", text, StringComparison.Ordinal);
    }

    [Fact]
    public void CarriesAdditionalStatusWordsThrough()
    {
        CipResponse response = CipResponse.Parse([0x90, 0x00, 0x1E, 0x01, 0x34, 0x12]);

        Assert.Equal(new ushort[] { 0x1234 }, response.AdditionalStatus);
        Assert.Contains("0x1234", response.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesATruncatedReplyRatherThanGuessing()
    {
        Assert.Throws<EnipException>(() => CipResponse.Parse([0x8E, 0x00]));
        Assert.Throws<EnipException>(() => CipResponse.Parse([0x8E, 0x00, 0x00, 0x04, 0x01]));
    }

    [Fact]
    public void RefusesAnInterfaceConfigurationTooShortToHoldFiveAddresses()
    {
        Assert.Throws<EnipException>(() => InterfaceConfig.Parse(new byte[12]));
    }

    /// <summary>
    /// A reading has to be checked before it can be used. An unconstrained caller that forgets is
    /// the way a refusal turns into a decoded zero and a device gets recorded as commissioned.
    /// </summary>
    [Fact]
    public void ARefusedReadingWillNotHandOverAValue()
    {
        CipReading<ConfigMethod> refused = CipReading<ConfigMethod>.Failed(
            CipResponse.Parse([0x8E, 0x00, CipGeneralStatus.AttributeNotSupported, 0x00]));

        Assert.False(refused.IsSuccess);
        Assert.Throws<InvalidOperationException>(() => refused.Value);
    }
}
