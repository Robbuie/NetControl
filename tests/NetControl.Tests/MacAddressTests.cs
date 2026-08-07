using NetControl.Core;
using Xunit;

namespace NetControl.Tests;

public class MacAddressTests
{
    [Theory]
    [InlineData("00:00:BC:5E:11:01")]
    [InlineData("00-00-bc-5e-11-01")]
    [InlineData("0000BC5E1101")]
    [InlineData("0000.bc5e.1101")]
    [InlineData(" 00 00 BC 5E 11 01 ")]
    public void AcceptsEveryFormAPersonMightType(string text)
    {
        Assert.True(MacAddress.TryParse(text, out MacAddress mac));
        Assert.Equal("00:00:BC:5E:11:01", mac.ToString());
    }

    /// <summary>
    /// The bug this type exists to prevent: a plan imported with hyphens never matching a device
    /// logged with colons, presenting as "the tool just ignores my device".
    /// </summary>
    [Fact]
    public void DifferentPunctuationIsTheSameDictionaryKey()
    {
        var map = new Dictionary<MacAddress, string>
        {
            [MacAddress.Parse("00-00-bc-5e-11-01")] = "conveyor 3",
        };

        Assert.True(map.ContainsKey(MacAddress.Parse("00:00:BC:5E:11:01")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("00:00:BC:5E:11")]
    [InlineData("00:00:BC:5E:11:01:02")]
    [InlineData("00:00:BC:5E:11:0G")]
    [InlineData("not a mac")]
    public void RejectsWhatIsNotAMac(string text)
    {
        Assert.False(MacAddress.TryParse(text, out _));
    }

    [Fact]
    public void RejectsNull() => Assert.False(MacAddress.TryParse(null, out _));

    [Fact]
    public void RoundTripsThroughBytes()
    {
        byte[] original = [0x00, 0x1D, 0x9C, 0xC7, 0xB0, 0x70];
        var mac = new MacAddress(original);

        Assert.Equal(original, mac.ToArray());
        Assert.Equal("00:1D:9C:C7:B0:70", mac.ToString());
    }

    [Fact]
    public void ExposesTheOuiForVendorLookup()
    {
        Assert.Equal(Frames.RockwellOui, MacAddress.Parse(Frames.DeviceMac).Oui);
        Assert.False(MacAddress.Parse(Frames.DeviceMac).IsLocallyAdministered);
    }

    [Fact]
    public void RecognisesAddressesThatCannotBeADevice()
    {
        Assert.True(MacAddress.Empty.IsEmpty);
        Assert.True(MacAddress.Parse("FF:FF:FF:FF:FF:FF").IsBroadcast);
        Assert.True(MacAddress.Parse("01:00:5E:00:00:01").IsMulticast);
        Assert.True(MacAddress.Parse("02:00:00:00:00:01").IsLocallyAdministered);
        Assert.False(MacAddress.Parse(Frames.DeviceMac).IsMulticast);
    }

    [Fact]
    public void ParseThrowsWithAMessageThatShowsTheExpectedForm()
    {
        FormatException ex = Assert.Throws<FormatException>(() => MacAddress.Parse("nope"));
        Assert.Contains("00:1D:9C:C7:B0:70", ex.Message, StringComparison.Ordinal);
    }
}
