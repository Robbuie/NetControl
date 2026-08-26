using NetControl.Core;
using NetControl.Core.Oui;
using Xunit;

namespace NetControl.Tests;

/// <summary>
/// The OUI table is one of the few places in this product where being wrong is quiet: a bad vendor
/// name looks exactly like a good one, gets written into the project file at import time, and
/// turns up months later in a commissioning report. So these tests check the mechanics the eye
/// cannot - that the writer and reader agree, that longest-prefix really wins, and that an address
/// which cannot carry a vendor is refused rather than guessed at.
/// </summary>
public class OuiDatabaseTests
{
    /// <summary>
    /// The registry hands MA-S blocks out of MA-L ranges it keeps in its own name, so a 24-bit
    /// first search would answer "IEEE Registration Authority" for every small vendor - the ones
    /// most likely to be the odd gateway in a panel nobody recognises. This is the behaviour the
    /// whole three-table layout exists for.
    /// </summary>
    [Fact]
    public void TheLongestMatchingPrefixWins()
    {
        var database = Pack(
            new OuiAssignment(0x70B3D5000000, 24, "IEEE Registration Authority"),
            new OuiAssignment(0x70B3D51F2000, 36, "Example Small Vendor"),
            new OuiAssignment(0x0055DA000000, 28, "Example Medium Vendor"));

        Assert.Equal("Example Small Vendor", database.Lookup(MacAddress.Parse("70:B3:D5:1F:20:01")));
        Assert.Equal("Example Small Vendor", database.Lookup(MacAddress.Parse("70:B3:D5:1F:2F:FF")));

        // One nibble outside the 36-bit block, so it falls back to the 24-bit parent.
        Assert.Equal("IEEE Registration Authority", database.Lookup(MacAddress.Parse("70:B3:D5:1F:30:01")));
        Assert.Equal("IEEE Registration Authority", database.Lookup(MacAddress.Parse("70:B3:D5:99:00:01")));

        Assert.Equal("Example Medium Vendor", database.Lookup(MacAddress.Parse("00:55:DA:01:02:03")));
        Assert.Equal("Example Medium Vendor", database.Lookup(MacAddress.Parse("00:55:DA:0F:FF:FF")));

        // Inside the same 24-bit range but outside the 28-bit block, and no MA-L parent is packed.
        Assert.Null(database.Lookup(MacAddress.Parse("00:55:DA:F0:00:01")));
    }

    [Fact]
    public void ReportsHowManyBitsTheMatchConsumed()
    {
        var database = Pack(
            new OuiAssignment(0x70B3D5000000, 24, "IEEE Registration Authority"),
            new OuiAssignment(0x70B3D51F2000, 36, "Example Small Vendor"));

        Assert.Equal("Example Small Vendor", database.Lookup(MacAddress.Parse("70:B3:D5:1F:20:01"), out int narrow));
        Assert.Equal(36, narrow);

        string? wideMatch = database.Lookup(MacAddress.Parse("70:B3:D5:99:00:01"), out int wide);
        Assert.Equal("IEEE Registration Authority", wideMatch);
        Assert.Equal(24, wide);

        Assert.Null(database.Lookup(MacAddress.Parse("AA:BB:CC:DD:EE:F0"), out int none));
        Assert.Equal(0, none);
    }

    /// <summary>
    /// A locally administered address was set by software - a hypervisor, a NIC team, a managed
    /// switch. Whatever 24-bit block its bytes collide with says nothing about who made the
    /// hardware, so answering with that block's registrant would be a fabrication that then gets
    /// stored in the project file.
    /// </summary>
    [Theory]
    [InlineData("02:00:BC:5E:11:01")]   // same OUI bytes as Rockwell, but the local bit is set
    [InlineData("0A:00:BC:5E:11:01")]
    public void RefusesToGuessAtALocallyAdministeredAddress(string text)
    {
        var database = Pack(new OuiAssignment(0x0000BC000000, 24, "Rockwell Automation"));

        Assert.Null(database.Lookup(MacAddress.Parse(text)));
    }

    [Theory]
    [InlineData("00:00:00:00:00:00")]
    [InlineData("FF:FF:FF:FF:FF:FF")]
    [InlineData("01:00:5E:00:00:01")]
    public void RefusesAddressesThatAreNotAnAdapter(string text)
    {
        var database = Pack(new OuiAssignment(0x0000BC000000, 24, "Rockwell Automation"));

        Assert.Null(database.Lookup(MacAddress.Parse(text)));
    }

    [Fact]
    public void AnUnknownPrefixIsNullRatherThanAPlaceholder()
    {
        var database = Pack(new OuiAssignment(0x0000BC000000, 24, "Rockwell Automation"));

        Assert.Null(database.Lookup(MacAddress.Parse("AA:BB:CC:DD:EE:F0")));
        Assert.Null(OuiDatabase.Empty.Lookup(MacAddress.Parse(Frames.DeviceMac)));
    }

    /// <summary>
    /// Every assignment written comes back with the same prefix, length and name. This is the test
    /// that would catch an endianness or record-width slip in <c>OuiPackFormat</c>, which is
    /// otherwise invisible until a real lookup returns a neighbouring vendor.
    /// </summary>
    [Fact]
    public void RoundTripsEveryAssignment()
    {
        OuiAssignment[] original =
        [
            new(0x000000000000, 24, "Lowest Possible Prefix"),
            new(0x0000BC000000, 24, "Rockwell Automation"),
            new(0xFFFFFF000000, 24, "Highest Possible Prefix"),
            new(0x0055DA000000, 28, "Example Medium Vendor"),
            new(0x70B3D51F2000, 36, "Example Small Vendor"),
            new(0xFFFFFFFFF000, 36, "Highest Possible Small Prefix"),
        ];

        var database = Pack(original);

        Assert.Equal(original.Length, database.Count);
        Assert.Equal(3, database.CountOf(24));
        Assert.Equal(1, database.CountOf(28));
        Assert.Equal(2, database.CountOf(36));

        foreach (var assignment in original)
        {
            Assert.Contains(assignment, database.Enumerate());
        }
    }

    /// <summary>
    /// A vendor holding many blocks must be stored once. This is not a micro-optimisation: it is
    /// the difference between the packed registry being a few hundred kilobytes and being megabytes,
    /// and megabytes is the point at which someone argues for shipping it as a loose file.
    /// </summary>
    [Fact]
    public void SharedVendorNamesAreStoredOnce()
    {
        var repeated = new List<OuiAssignment>();
        for (uint i = 0; i < 500; i++)
        {
            repeated.Add(new OuiAssignment((ulong)i << 24, 24, "One Vendor With Many Blocks"));
        }

        var database = Pack([.. repeated]);
        var names = database.Enumerate().Select(assignment => assignment.Organization).ToList();

        Assert.Equal(500, names.Count);

        // Every record has to point at the same blob offset for these to be the same instance:
        // the reader caches on offset, so 500 separate copies of the name would produce 500
        // separate strings. Reference equality is therefore a direct test of the dedupe.
        Assert.All(names, name => Assert.Same(names[0], name));
    }

    [Fact]
    public void KeepsTheSourceDateItWasGiven()
    {
        var date = new DateTime(2026, 3, 14, 0, 0, 0, DateTimeKind.Utc);

        using var stream = new MemoryStream();
        OuiPackWriter.Write(stream, date, [new OuiAssignment(0x0000BC000000, 24, "Rockwell Automation")]);
        stream.Position = 0;

        Assert.Equal(date, OuiDatabase.Load(stream).SourceDateUtc);
    }

    [Fact]
    public void RefusesSomethingThatIsNotAPackedTable()
    {
        byte[] notAPackedTable = [1, 2, 3, 4, 5, 6, 7, 8];
        using var rubbish = new MemoryStream(notAPackedTable);

        Assert.Throws<OuiException>(() => OuiDatabase.Load(rubbish));
    }

    /// <summary>
    /// The table shipped in the repository, read out of the built assembly. This is the test that
    /// fails if the embedded resource goes missing from the build or the packer writes a format
    /// this build cannot read - both of which otherwise present as "every device is an unknown
    /// vendor", which looks like a data problem rather than a build problem.
    ///
    /// The assertions are on a distinctive substring rather than the exact registered name,
    /// because that name is data this repository does not own. IEEE re-spells registrants as they
    /// merge and rebrand, and a test that breaks on "Rockwell Automation" becoming "Rockwell
    /// Automation, Inc." would train whoever refreshes the table to edit the test until it passes
    /// - which is exactly the habit that lets a real regression through. This still fails if the
    /// resource is missing, unreadable, or looking up the wrong prefix.
    /// </summary>
    [Fact]
    public void TheBundledTableResolvesTheDeviceInTheTestFrames()
    {
        var bundled = OuiDatabase.Bundled;

        Assert.True(bundled.Count > 0, "The embedded OUI table is empty.");
        AssertVendorContains(bundled, Frames.DeviceMac, "Rockwell");
        AssertVendorContains(bundled, "00:1D:9C:C7:B0:70", "Rockwell");
        AssertVendorContains(bundled, "08:00:06:11:22:33", "Siemens");
    }

    private static void AssertVendorContains(OuiDatabase database, string mac, string expected)
    {
        string? actual = database.Lookup(MacAddress.Parse(mac));

        Assert.False(actual is null, $"{mac} resolved to no vendor at all; expected one naming {expected}.");
        Assert.Contains(expected, actual, StringComparison.OrdinalIgnoreCase);
    }

    private static OuiDatabase Pack(params OuiAssignment[] assignments)
    {
        var stream = new MemoryStream();
        OuiPackWriter.Write(stream, DateTime.UnixEpoch, assignments);
        stream.Position = 0;

        return OuiDatabase.Load(stream);
    }
}
