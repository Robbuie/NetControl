using System.Net;
using Microsoft.Data.Sqlite;
using NetControl.App.Serving;
using NetControl.App.ViewModels;
using NetControl.Core;
using NetControl.Core.Dhcp;
using NetControl.Core.Discovery;
using NetControl.Core.Oui;
using NetControl.Core.Persistence;
using Xunit;

namespace NetControl.Tests;

/// <summary>
/// The scan history (schema version 2): kept, append-only, chosen by subnet, carried by Save As,
/// and compared with the next scan of the same subnet from the window.
/// </summary>
public sealed class ScanHistoryTests : IDisposable
{
    private static readonly MacAddress DeviceMac = MacAddress.Parse("00:1D:9C:C7:B0:70");
    private static readonly IPAddress Mask24 = IPAddress.Parse("255.255.255.0");

    private readonly ProjectStore _project = ProjectStore.CreateInMemory("Test");

    public void Dispose() => _project.Dispose();

    [Fact]
    public void KeepsAScanAndEverythingThatAnsweredIt()
    {
        long run = _project.Scans.Record(
            Events.At, "[12] Ethernet", IPAddress.Parse("192.168.1.10"), Mask24, [Sighting("192.168.1.51", 0x00C0FFEE)]);

        ScanSightingRecord seen = Assert.Single(_project.Scans.Sightings(run));
        Assert.Equal(IPAddress.Parse("192.168.1.51"), seen.Address);
        Assert.Equal(0x00C0FFEEu, seen.Serial);
        Assert.Equal(DeviceMac, seen.Mac);
        Assert.Equal("11.03", seen.Revision);
        Assert.Equal(1, Assert.Single(_project.Scans.Runs()).Answered);
    }

    /// <summary>A scan of a different segment is not a "before", however recent it is.</summary>
    [Fact]
    public void ComparesOnlyWithAScanOfTheSameSubnet()
    {
        long ours = _project.Scans.Record(Events.At, "[12] Ethernet", IPAddress.Parse("192.168.1.10"), Mask24, []);
        _project.Scans.Record(Events.At.AddMinutes(5), "[14] USB", IPAddress.Parse("10.0.0.5"), Mask24, []);

        Assert.True(Ipv4Subnet.TryCreate(IPAddress.Parse("192.168.1.77"), Mask24, out Ipv4Subnet subnet));

        Assert.Equal(ours, _project.Scans.LatestOn(subnet)?.Id);
    }

    /// <summary>What answered on a given day is part of the record: the database refuses to rewrite it.</summary>
    [Fact]
    public void TheDatabaseRefusesToRewriteTheHistory()
    {
        using var temp = new TempProject();
        using (ProjectStore store = temp.Open())
        {
            store.Scans.Record(Events.At, "[12] Ethernet", IPAddress.Parse("192.168.1.10"), Mask24,
                [Sighting("192.168.1.51", 1)]);
        }

        using var connection = new SqliteConnection($"Data Source={temp.FilePath};Pooling=False");
        connection.Open();

        foreach (string sql in new[]
        {
            "UPDATE ScanSighting SET Address = '10.0.0.1';",
            "DELETE FROM ScanSighting;",
            "UPDATE ScanRun SET NicName = 'x';",
            "DELETE FROM ScanRun;",
        })
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
        }
    }

    /// <summary>
    /// A project file written by a build that only knew schema version 1 - every file in the field
    /// today - opens, migrates to version 2, keeps what it had, and can then keep a scan history.
    /// </summary>
    [Fact]
    public void AVersionOneFileMigratesAndKeepsItsRecord()
    {
        using var temp = new TempProject();

        using (var connection = new SqliteConnection($"Data Source={temp.FilePath};Pooling=False"))
        {
            connection.Open();

            using SqliteCommand create = connection.CreateCommand();
            create.CommandText = SchemaMigrations.Steps[0]
                + "INSERT INTO Project (Id, Name, SchemaVersion, CreatedUtc) VALUES (1, 'Old', 1, '2026-08-01T00:00:00.0000000Z');"
                + "INSERT INTO Event (Utc, Severity, Category, Message) VALUES ('2026-08-01T00:00:00.0000000Z', 'info', 'app', 'From v1.');"
                + "PRAGMA user_version = 1;";
            create.ExecuteNonQuery();
        }

        using ProjectStore store = temp.Open();

        Assert.Equal(ProjectStore.SupportedSchemaVersion, store.Project.SchemaVersion);
        Assert.Contains(store.Events.All(), e => e.Message == "From v1.");

        store.Scans.Record(Events.At, "[12] Ethernet", IPAddress.Parse("192.168.1.10"), Mask24, []);
        Assert.Equal(1, store.Scans.Count);
    }

    [Fact]
    public void SaveAsCarriesTheHistory()
    {
        _project.Scans.Record(Events.At, "[12] Ethernet", IPAddress.Parse("192.168.1.10"), Mask24,
            [Sighting("192.168.1.51", 1), Sighting("192.168.1.52", 2)]);

        using var temp = new TempProject();
        using ProjectStore saved = _project.SaveAs(temp.FilePath);

        ScanRunRecord run = Assert.Single(saved.Scans.Runs());
        Assert.Equal(2, saved.Scans.Sightings(run.Id).Count);
    }

    /// <summary>The first scan of a subnet has nothing to compare with: it is kept, and says nothing about change.</summary>
    [Fact]
    public async Task TheFirstScanOfASubnetIsKeptAndComparedWithNothing()
    {
        MainViewModel viewModel = Build(new FakeIdentityScanner(Identities.Reply()));
        SelectAdapter(viewModel);

        await viewModel.ScanCommand.ExecuteAsync(null);

        Assert.Equal(1, _project.Scans.Count);
        Assert.Null(viewModel.Scan.ChangesSummary);
        Assert.False(viewModel.Scan.HasChanges);
    }

    /// <summary>
    /// The second scan sees that a different product is now at an address another device held last
    /// time - on screen, and in the record as a Warn of its own.
    /// </summary>
    [Fact]
    public async Task TheNextScanSaysWhatChangedAndRecordsIt()
    {
        _project.Scans.Record(
            Events.At.AddDays(-1), "[12] Ethernet", IPAddress.Parse("192.168.1.10"), Mask24,
            [Sighting("192.168.1.51", 0x11111111, product: 55, name: "PowerFlex 525")]);

        MainViewModel viewModel = Build(new FakeIdentityScanner(Identities.Reply()));
        SelectAdapter(viewModel);

        await viewModel.ScanCommand.ExecuteAsync(null);

        InventoryChange change = Assert.Single(viewModel.Scan.Changes);
        Assert.Equal(InventoryChangeKind.DifferentDevice, change.Kind);
        Assert.NotNull(viewModel.Scan.ChangesSummary);

        Assert.Contains(
            _project.Events.All(),
            e => e.Severity == EventSeverity.Warn && e.Message.Contains("PowerFlex 525", StringComparison.Ordinal));
        Assert.Equal(2, _project.Scans.Count);
    }

    private static ScanSightingRecord Sighting(
        string address, uint serial, ushort product = 167, string name = "1734-AENT/B") => new()
        {
            Address = IPAddress.Parse(address),
            Mac = DeviceMac,
            VendorId = 1,
            DeviceType = 12,
            ProductCode = product,
            Revision = "11.03",
            Serial = serial,
            ProductName = name,
        };

    private static void SelectAdapter(MainViewModel viewModel)
    {
        viewModel.InterfaceBar.ApplyInventory([Nics.Healthy()]);
        viewModel.InterfaceBar.SelectedAdapter = viewModel.InterfaceBar.Adapters[0];
    }

    private MainViewModel Build(FakeIdentityScanner scanner)
    {
        var policy = new StaticMapPolicy();
        var nics = new FakeNicInventory();

        var discovery = new DeviceDiscovery(
            scanner,
            FakeArpLookup.Holding("192.168.1.51", DeviceMac),
            new FakeOuiLookup());

        return new MainViewModel(
            new ImmediateDispatcher(),
            nics,
            new StubPreflight(),
            new ServerController(nics, policy),
            policy,
            OuiDatabase.Empty,
            _project,
            new TestTimeProvider(Events.At),
            discovery: discovery);
    }
}
