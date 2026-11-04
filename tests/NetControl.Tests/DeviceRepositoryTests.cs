using System.Net;
using Microsoft.Data.Sqlite;
using NetControl.Core;
using NetControl.Core.Dhcp;
using NetControl.Core.Persistence;
using Xunit;

namespace NetControl.Tests;

/// <summary>
/// The plan, stored and read back.
///
/// The interesting cases are all about incomplete or wrong plans, because that is what a plan
/// looks like at 7am on a commissioning day: half the MACs are copied off labels, one of the
/// masks has a typo, and two rows have no address yet.
/// </summary>
public class DeviceRepositoryTests
{
    private static readonly MacAddress Mac = MacAddress.Parse("00:00:BC:5E:11:01");

    private static DeviceRecord Planned(string? ip = "192.168.1.51", string? mask = "255.255.255.0") => new()
    {
        Mac = Mac,
        PlannedIp = ip is null ? null : IPAddress.Parse(ip),
        PlannedMask = mask is null ? null : IPAddress.Parse(mask),
        PlannedGateway = IPAddress.Parse("192.168.1.1"),
        HostName = "conveyor-3",
        PanelRef = "MCC-7 / drawing E-204",
        Role = "PowerFlex 525 conveyor 3",
        Vendor = "Rockwell Automation",
        Notes = "Swapped 2026-03-14",
    };

    [Fact]
    public void RoundTripsEveryColumn()
    {
        using var temp = new TempProject();
        using ProjectStore store = temp.Open();

        long id = store.Devices.Upsert(Planned() with { Quirks = DeviceQuirks.RequiresResetToApply });

        DeviceRecord? read = store.Devices.Find(Mac);

        Assert.NotNull(read);
        Assert.Equal(id, read.Id);
        Assert.Equal(Mac, read.Mac);
        Assert.Equal(IPAddress.Parse("192.168.1.51"), read.PlannedIp);
        Assert.Equal(IPAddress.Parse("255.255.255.0"), read.PlannedMask);
        Assert.Equal(IPAddress.Parse("192.168.1.1"), read.PlannedGateway);
        Assert.Equal("conveyor-3", read.HostName);
        Assert.Equal("MCC-7 / drawing E-204", read.PanelRef);
        Assert.Equal("PowerFlex 525 conveyor 3", read.Role);
        Assert.Equal("Rockwell Automation", read.Vendor);
        Assert.Equal(DeviceQuirks.RequiresResetToApply, read.Quirks);
        Assert.Equal("Swapped 2026-03-14", read.Notes);
    }

    /// <summary>
    /// MACs are stored canonically, so the punctuation somebody typed cannot decide whether a
    /// device matches its plan. This is the bug class <see cref="MacAddress"/> exists to remove,
    /// and it has to hold across a save and a reload too.
    /// </summary>
    [Fact]
    public void MatchesADeviceWhateverPunctuationWasTyped()
    {
        using var temp = new TempProject();
        using ProjectStore store = temp.Open();

        store.Devices.Upsert(Planned() with { Mac = MacAddress.Parse("00-00-bc-5e-11-01") });

        Assert.NotNull(store.Devices.Find(MacAddress.Parse("0000.BC5E.1101")));
        Assert.Equal(1, store.Devices.Count);
    }

    /// <summary>
    /// A re-import of a corrected CSV must be able to blank a field. Merging instead of replacing
    /// would leave yesterday's wrong gateway sitting in the plan with nothing to show it was ever
    /// there.
    /// </summary>
    [Fact]
    public void ReimportingReplacesTheRowRatherThanMergingIntoIt()
    {
        using var temp = new TempProject();
        using ProjectStore store = temp.Open();

        long first = store.Devices.Upsert(Planned());
        long second = store.Devices.Upsert(new DeviceRecord { Mac = Mac, PlannedIp = IPAddress.Parse("192.168.1.60") });

        DeviceRecord? read = store.Devices.Find(Mac);

        Assert.Equal(first, second);
        Assert.Equal(1, store.Devices.Count);
        Assert.Equal(IPAddress.Parse("192.168.1.60"), read!.PlannedIp);
        Assert.Null(read.PlannedGateway);
        Assert.Null(read.Notes);
    }

    /// <summary>A CSV has no quirks column; re-importing must not forget what Set static learned.</summary>
    [Fact]
    public void ReimportingKeepsTheQuirks()
    {
        using var temp = new TempProject();
        using ProjectStore store = temp.Open();

        store.Devices.Upsert(Planned() with { Quirks = DeviceQuirks.RequiresResetToApply });
        store.Devices.Upsert(Planned());

        Assert.Equal(DeviceQuirks.RequiresResetToApply, store.Devices.Find(Mac)!.Quirks);
    }

    [Fact]
    public void ImportsAWholePlanInOneGo()
    {
        using var temp = new TempProject();
        using ProjectStore store = temp.Open();

        List<DeviceRecord> plan = [.. Enumerable.Range(1, 12).Select(i => new DeviceRecord
        {
            Mac = MacAddress.Parse($"00:00:BC:5E:11:{i:X2}"),
            PlannedIp = IPAddress.Parse($"192.168.1.{50 + i}"),
            PlannedMask = IPAddress.Parse("255.255.255.0"),
        })];

        Assert.Equal(12, store.Devices.UpsertRange(plan));
        Assert.Equal(12, store.Devices.Count);
    }

    [Fact]
    public void RefusesAMacThatCannotIdentifyADevice()
    {
        using var temp = new TempProject();
        using ProjectStore store = temp.Open();

        Assert.Throws<PersistenceException>(
            () => store.Devices.Upsert(new DeviceRecord { Mac = MacAddress.Parse("FF:FF:FF:FF:FF:FF") }));
    }

    /// <summary>
    /// A row that will not become a servable assignment is reported by name, at the desk, rather
    /// than discovered by watching a panel stay dark.
    /// </summary>
    [Fact]
    public void NamesThePlannedRowsThatWillNeverBeServed()
    {
        using var temp = new TempProject();
        using ProjectStore store = temp.Open();

        store.Devices.Upsert(Planned());
        store.Devices.Upsert(Planned(mask: "255.255.0.255") with { Mac = MacAddress.Parse("00:00:BC:5E:11:02") });

        IReadOnlyList<DeviceAssignment> assignments = store.Devices.PlannedAssignments(out IReadOnlyList<string> problems);

        Assert.Single(assignments);
        Assert.Equal(IPAddress.Parse("192.168.1.51"), assignments[0].Ip);

        string problem = Assert.Single(problems);
        Assert.Contains("00:00:BC:5E:11:02", problem, StringComparison.Ordinal);
        Assert.Contains("contiguous", problem, StringComparison.Ordinal);
    }

    /// <summary>
    /// A MAC written down with the address still to be decided is a plan in progress, not a
    /// problem. It simply is not servable yet, and saying otherwise would train people to ignore
    /// the warnings that matter.
    /// </summary>
    [Fact]
    public void AnUnaddressedRowIsNotAProblem()
    {
        using var temp = new TempProject();
        using ProjectStore store = temp.Open();

        store.Devices.Upsert(new DeviceRecord { Mac = Mac, PanelRef = "MCC-7" });

        IReadOnlyList<DeviceAssignment> assignments = store.Devices.PlannedAssignments(out IReadOnlyList<string> problems);

        Assert.Empty(assignments);
        Assert.Empty(problems);
    }

    [Fact]
    public void DeletesAPlannedDeviceThatWasNeverServed()
    {
        using var temp = new TempProject();
        using ProjectStore store = temp.Open();

        store.Devices.Upsert(Planned());

        Assert.True(store.Devices.Delete(Mac));
        Assert.False(store.Devices.Delete(Mac));
        Assert.Equal(0, store.Devices.Count);
    }

    /// <summary>
    /// Once a device is in the commissioning record it stays there. The Event table is
    /// append-only, so deleting the device row it points at would leave the record referring to a
    /// device that never existed.
    /// </summary>
    [Fact]
    public void WillNotDeleteADeviceThatIsAlreadyInTheRecord()
    {
        using var temp = new TempProject();
        using ProjectStore store = temp.Open();

        long id = store.Devices.Upsert(Planned());
        store.Assignments.Record(id, IPAddress.Parse("192.168.1.51"), 12, "Ethernet", DateTimeOffset.UtcNow);

        var ex = Assert.Throws<PersistenceException>(() => store.Devices.Delete(Mac));

        Assert.Contains("commissioning record", ex.Message, StringComparison.Ordinal);
        Assert.NotNull(store.Devices.Find(Mac));
    }

    /// <summary>
    /// A project file is a SQLite database, so sooner or later somebody opens it in a browser tool
    /// and fixes something at 2am. A cell they got wrong must degrade to "this row is not
    /// servable", not to "this project will not open".
    /// </summary>
    [Fact]
    public void SurvivesAHandEditedAddressCell()
    {
        using var temp = new TempProject();
        using ProjectStore store = temp.Open();

        store.Devices.Upsert(Planned(ip: "192.168.1.51"));

        using (SqliteConnection connection = OpenRaw(temp.FilePath))
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE Device SET PlannedIp = 'not an address';";
            command.ExecuteNonQuery();
        }

        using ProjectStore reopened = temp.Open();
        DeviceRecord? read = reopened.Devices.Find(Mac);

        Assert.NotNull(read);
        Assert.Null(read.PlannedIp);
        Assert.False(read.IsPlanned);
    }

    /// <summary>
    /// The whole reason <see cref="DeviceRepository.Update"/> exists. Upsert is keyed on MAC, so
    /// correcting a MAC through it would leave the wrong row sitting in the plan next to the right
    /// one - and the wrong one would still be served.
    /// </summary>
    [Fact]
    public void CorrectsAMistypedMacInPlaceRatherThanAddingASecondRow()
    {
        using var temp = new TempProject();
        using ProjectStore store = temp.Open();

        MacAddress mistyped = MacAddress.Parse("00:00:BC:5E:11:10");
        long id = store.Devices.Upsert(Planned() with { Mac = mistyped });

        Assert.True(store.Devices.Update(Planned() with { Id = id, Mac = Mac }));

        Assert.Equal(1, store.Devices.Count);
        Assert.Null(store.Devices.Find(mistyped));
        Assert.Equal(id, store.Devices.Find(Mac)?.Id);
    }

    [Fact]
    public void UpdateBlanksAFieldThatWasCleared()
    {
        using var temp = new TempProject();
        using ProjectStore store = temp.Open();

        long id = store.Devices.Upsert(Planned());

        Assert.True(store.Devices.Update(Planned() with { Id = id, PlannedGateway = null, Notes = null }));

        DeviceRecord? read = store.Devices.Find(Mac);
        Assert.NotNull(read);
        Assert.Null(read.PlannedGateway);
        Assert.Null(read.Notes);
    }

    /// <summary>
    /// Same rule as <see cref="DeviceRepository.Delete(MacAddress)"/>, for the same reason: <c>Event</c> rows
    /// point at this Id, and renaming it would rewrite what those events say happened. Editing the
    /// rest of the row is still fine - only the identity is frozen.
    /// </summary>
    [Fact]
    public void WillNotChangeTheMacOfADeviceThatIsAlreadyInTheRecord()
    {
        using var temp = new TempProject();
        using ProjectStore store = temp.Open();

        long id = store.Devices.Upsert(Planned());
        store.Assignments.Record(id, IPAddress.Parse("192.168.1.51"), 12, "Ethernet", DateTimeOffset.UtcNow);

        var ex = Assert.Throws<PersistenceException>(() =>
            store.Devices.Update(Planned() with { Id = id, Mac = MacAddress.Parse("00:00:BC:5E:11:99") }));

        Assert.Contains("commissioning record", ex.Message, StringComparison.Ordinal);
        Assert.Equal(Mac, store.Devices.Find(Mac)?.Mac);

        // The row is not frozen, only its identity.
        Assert.True(store.Devices.Update(Planned() with { Id = id, HostName = "conveyor-3a" }));
        Assert.Equal("conveyor-3a", store.Devices.Find(Mac)?.HostName);
    }

    /// <summary>
    /// Two rows cannot share a MAC - the plan is keyed on it. Caught with a sentence rather than
    /// left to the UNIQUE constraint, which surfaces as "SQLite Error 19".
    /// </summary>
    [Fact]
    public void WillNotMoveARowOntoAMacAnotherRowAlreadyHas()
    {
        using var temp = new TempProject();
        using ProjectStore store = temp.Open();

        MacAddress other = MacAddress.Parse("00:00:BC:5E:11:02");
        long id = store.Devices.Upsert(Planned());
        store.Devices.Upsert(Planned() with { Mac = other });

        var ex = Assert.Throws<PersistenceException>(() =>
            store.Devices.Update(Planned() with { Id = id, Mac = other }));

        Assert.Contains("already in the plan", ex.Message, StringComparison.Ordinal);
        Assert.Equal(2, store.Devices.Count);
    }

    /// <summary>A row deleted underneath an open grid. Reported, not thrown - the caller re-inserts.</summary>
    [Fact]
    public void UpdateReportsARowThatIsNoLongerThere()
    {
        using var temp = new TempProject();
        using ProjectStore store = temp.Open();

        Assert.False(store.Devices.Update(Planned() with { Id = 4242 }));
    }

    // ---- Rows awaiting their MAC (PLAN-IMPORT.md F1) ----------------------------------------
    //
    // The project IP workbook comes off the drawings: names and addresses, no MACs. Those rows go
    // in, are never served, are matched by their planned address until the MAC arrives, and must
    // not lose a MAC filled in from the wire when the workbook is imported again.

    private static DeviceRecord Awaiting(string ip = "192.168.1.51", string host = "SF101RC01") => new()
    {
        Mac = MacAddress.Empty,
        PlannedIp = IPAddress.Parse(ip),
        PlannedMask = IPAddress.Parse("255.255.255.0"),
        HostName = host,
        PanelRef = "SF101 ES1-9",
    };

    [Fact]
    public void StoresARowAwaitingItsMac()
    {
        using var temp = new TempProject();
        using ProjectStore store = temp.Open();

        long id = store.Devices.Upsert(Awaiting());

        DeviceRecord? read = store.Devices.Get(id);
        Assert.NotNull(read);
        Assert.True(read.IsAwaitingMac);
        Assert.Equal(IPAddress.Parse("192.168.1.51"), read.PlannedIp);
        Assert.Equal("SF101RC01", read.HostName);

        // The empty MAC is not a key: it finds nothing, rather than every awaiting row.
        Assert.Null(store.Devices.Find(MacAddress.Empty));
    }

    /// <summary>
    /// NULL, not 00:00:00:00:00:00. Stored as the all-zero MAC, the second awaiting row would trip
    /// the UNIQUE constraint on the first.
    /// </summary>
    [Fact]
    public void HoldsAnyNumberOfRowsAwaitingTheirMac()
    {
        using var temp = new TempProject();
        using ProjectStore store = temp.Open();

        store.Devices.UpsertRange([Awaiting("192.168.1.51", "RC01"), Awaiting("192.168.1.52", "RC02"), Awaiting("192.168.1.53", "RC03")]);

        Assert.Equal(3, store.Devices.Count);
        Assert.All(store.Devices.All(), row => Assert.True(row.IsAwaitingMac));
    }

    [Fact]
    public void RefusesARowWithNeitherAMacNorAnAddress()
    {
        using var temp = new TempProject();
        using ProjectStore store = temp.Open();

        var ex = Assert.Throws<PersistenceException>(
            () => store.Devices.Upsert(Awaiting() with { PlannedIp = null }));

        Assert.Contains("neither a MAC nor a planned address", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, store.Devices.Count);
    }

    /// <summary>Importing the workbook twice corrects the plan rather than doubling it.</summary>
    [Fact]
    public void ReimportingMatchesARowAwaitingItsMacByItsAddress()
    {
        using var temp = new TempProject();
        using ProjectStore store = temp.Open();

        long id = store.Devices.Upsert(Awaiting() with { Notes = "Rev1" });
        long again = store.Devices.Upsert(Awaiting() with { Notes = "Rev2" });

        Assert.Equal(id, again);
        Assert.Equal(1, store.Devices.Count);
        Assert.Equal("Rev2", store.Devices.Get(id)?.Notes);
    }

    /// <summary>
    /// The workbook has no MACs, so re-importing it must not throw away one that was filled in from
    /// the request log or a scan since - nor the vendor that came with it.
    /// </summary>
    [Fact]
    public void ReimportingTheWorkbookKeepsAMacFilledInSince()
    {
        using var temp = new TempProject();
        using ProjectStore store = temp.Open();

        long id = store.Devices.Upsert(Awaiting());
        Assert.True(store.Devices.Update(Awaiting() with { Id = id, Mac = Mac, Vendor = "FANUC" }));

        long again = store.Devices.Upsert(Awaiting() with { Notes = "Rev2" });

        DeviceRecord? read = store.Devices.Get(id);
        Assert.Equal(id, again);
        Assert.Equal(1, store.Devices.Count);
        Assert.NotNull(read);
        Assert.Equal(Mac, read.Mac);
        Assert.Equal("FANUC", read.Vendor);
        Assert.Equal("Rev2", read.Notes);
    }

    /// <summary>A CSV with MACs imported over the workbook fills the rows in, rather than adding beside them.</summary>
    [Fact]
    public void AMacArrivingForAnAwaitingAddressFillsThatRowIn()
    {
        using var temp = new TempProject();
        using ProjectStore store = temp.Open();

        long id = store.Devices.Upsert(Awaiting());
        long filled = store.Devices.Upsert(Awaiting() with { Mac = Mac, Vendor = "FANUC" });

        Assert.Equal(id, filled);
        Assert.Equal(1, store.Devices.Count);
        Assert.Equal(id, store.Devices.Find(Mac)?.Id);
    }

    /// <summary>
    /// Filling in a MAC is not renaming a device: the record never named a MAC for this row, so
    /// nothing it says is rewritten. The rule that freezes a MAC once it is in the record is about
    /// changing one, not about the first one arriving.
    /// </summary>
    [Fact]
    public void FillsInTheMacOfAnAwaitingRowEvenOnceItIsInTheRecord()
    {
        using var temp = new TempProject();
        using ProjectStore store = temp.Open();

        long id = store.Devices.Upsert(Awaiting());
        store.Events.Append(EventSeverity.Info, EventCategory.App, "Planned from the workbook.", deviceId: id);

        Assert.True(store.Devices.Update(Awaiting() with { Id = id, Mac = Mac }));
        Assert.Equal(id, store.Devices.Find(Mac)?.Id);

        // But once it has one, the old rule holds.
        Assert.Throws<PersistenceException>(() => store.Devices.Update(Awaiting() with { Id = id }));
    }

    /// <summary>
    /// Two rows already on the address: there is no telling which one a MAC-less row means, so it
    /// is refused rather than written over whichever came first.
    /// </summary>
    [Fact]
    public void RefusesToGuessWhichRowAnAwaitingDeviceMeans()
    {
        using var temp = new TempProject();
        using ProjectStore store = temp.Open();

        store.Devices.Upsert(Planned(ip: "192.168.1.51"));
        store.Devices.Upsert(Planned(ip: "192.168.1.51") with { Mac = MacAddress.Parse("00:00:BC:5E:11:02") });

        var ex = Assert.Throws<PersistenceException>(() => store.Devices.Upsert(Awaiting()));

        Assert.Contains("2 rows in the plan are already planned for 192.168.1.51", ex.Message, StringComparison.Ordinal);
        Assert.Equal(2, store.Devices.Count);
    }

    /// <summary>
    /// Not served, and not a problem either. "12 planned devices will not be served" on opening a
    /// freshly imported workbook would be a warning that is true of every row and useful about none.
    /// </summary>
    [Fact]
    public void AnAwaitingRowIsNeitherServedNorAProblem()
    {
        using var temp = new TempProject();
        using ProjectStore store = temp.Open();

        store.Devices.Upsert(Awaiting());

        Assert.Empty(store.Devices.PlannedAssignments(out IReadOnlyList<string> problems));
        Assert.Empty(problems);
        Assert.False(Awaiting().TryToAssignment(out _, out string? why));
        Assert.Contains("awaiting its MAC", why, StringComparison.Ordinal);
    }

    [Fact]
    public void DeletesARowAwaitingItsMacById()
    {
        using var temp = new TempProject();
        using ProjectStore store = temp.Open();

        long id = store.Devices.Upsert(Awaiting());

        Assert.True(store.Devices.Delete(id));
        Assert.Equal(0, store.Devices.Count);
        Assert.False(store.Devices.Delete(id));
    }

    private static SqliteConnection OpenRaw(string path)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }
}
