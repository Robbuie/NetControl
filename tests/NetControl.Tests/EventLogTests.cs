using Microsoft.Data.Sqlite;
using NetControl.Core;
using NetControl.Core.Persistence;
using Xunit;

namespace NetControl.Tests;

/// <summary>
/// The commissioning record.
///
/// The append-only tests here are the point of the whole class. Everything else is CRUD; the
/// guarantee that a row cannot be changed after the fact is what makes the log worth handing to
/// a customer.
/// </summary>
public class EventLogTests
{
    [Fact]
    public void RoundTripsAnEvent()
    {
        using var temp = new TempProject();
        using ProjectStore store = temp.Open();

        EventDetail detail = new EventDetail()
            .Add("xid", "FC5D1465")
            .Add("interfaceIndex", 12)
            .Add("broadcastFlag", true);

        store.Events.Append(
            EventSeverity.Warn,
            EventCategory.Dhcp,
            "00:00:BC:5E:11:01 is not in the plan.",
            "00:00:BC:5E:11:01",
            detail: detail);

        EventRecord row = Assert.Single(store.Events.All());

        Assert.Equal(EventSeverity.Warn, row.Severity);
        Assert.Equal(EventCategory.Dhcp, row.Category);
        Assert.Equal("00:00:BC:5E:11:01", row.Target);
        Assert.Null(row.DeviceId);
        Assert.Equal("""{"xid":"FC5D1465","interfaceIndex":12,"broadcastFlag":true}""", row.Detail);
    }

    /// <summary>
    /// Absence of a key means "not present". A detail column full of nulls is noise in the one
    /// place that has to stay readable.
    /// </summary>
    [Fact]
    public void LeavesTheDetailColumnNullWhenThereIsNothingToSay()
    {
        using ProjectStore store = ProjectStore.CreateInMemory();

        store.Events.Append(EventSeverity.Info, EventCategory.App, "Nothing to add.", detail: new EventDetail());

        Assert.Null(Assert.Single(store.Events.All()).Detail);
    }

    [Fact]
    public void DropsEmptyValuesButKeepsFalseAndZero()
    {
        EventDetail detail = new EventDetail()
            .Add("kept", "yes")
            .Add("dropped", (string?)null)
            .Add("alsoDropped", "   ")
            .Add("keptFalse", false)
            .Add("keptZero", 0L);

        Assert.Equal("""{"kept":"yes","keptFalse":false,"keptZero":0}""", detail.ToJson());
    }

    /// <summary>
    /// Timestamps are text, so the format has to be fixed-width or the ordering is wrong across a
    /// fractional-second boundary. Nobody would notice until a report listed events out of order.
    /// </summary>
    [Fact]
    public void OrdersChronologicallyEvenThoughTimestampsAreText()
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 8, 6, 9, 0, 0, TimeSpan.Zero));
        using var temp = new TempProject();
        using ProjectStore store = temp.Open(time: clock);

        store.Events.Append(EventSeverity.Info, EventCategory.Dhcp, "first");
        clock.Advance(TimeSpan.FromMilliseconds(1));
        store.Events.Append(EventSeverity.Info, EventCategory.Dhcp, "second");
        clock.Advance(TimeSpan.FromSeconds(1));
        store.Events.Append(EventSeverity.Info, EventCategory.Dhcp, "third");

        Assert.Equal(new[] { "first", "second", "third" }, store.Events.All().Select(e => e.Message));
        Assert.Equal(new[] { "third", "second", "first" }, store.Events.Recent().Select(e => e.Message));
    }

    [Fact]
    public void PullsOutOneSessionsSliceOfTheRecord()
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 8, 6, 9, 0, 0, TimeSpan.Zero));
        using ProjectStore store = ProjectStore.CreateInMemory(timeProvider: clock);

        store.Events.Append(EventSeverity.Info, EventCategory.App, "yesterday");
        clock.Advance(TimeSpan.FromHours(20));
        DateTimeOffset sessionStart = clock.GetUtcNow();
        store.Events.Append(EventSeverity.Info, EventCategory.Dhcp, "this session");

        Assert.Equal(new[] { "this session" }, store.Events.Since(sessionStart).Select(e => e.Message));
    }

    [Fact]
    public void FiltersByDevice()
    {
        using ProjectStore store = ProjectStore.CreateInMemory();

        long id = store.Devices.Upsert(new DeviceRecord { Mac = MacAddress.Parse("00:00:BC:5E:11:01") });
        store.Events.Append(EventSeverity.Info, EventCategory.Dhcp, "about the device", deviceId: id);
        store.Events.Append(EventSeverity.Info, EventCategory.App, "about nothing in particular");

        Assert.Equal(new[] { "about the device" }, store.Events.ForDevice(id).Select(e => e.Message));
    }

    /// <summary>
    /// Enforced by the database, not by everyone remembering. If a row could be quietly changed
    /// the log would be a story rather than a record.
    /// </summary>
    [Fact]
    public void TheDatabaseItselfRefusesToUpdateAnEventRow()
    {
        using var temp = new TempProject();

        using (ProjectStore store = temp.Open())
        {
            store.Events.Append(EventSeverity.Info, EventCategory.Dhcp, "as it happened");
        }

        var ex = Assert.Throws<SqliteException>(() => ExecuteRaw(temp.FilePath, "UPDATE Event SET Message = 'nope';"));

        Assert.Contains("append-only", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDatabaseItselfRefusesToDeleteAnEventRow()
    {
        using var temp = new TempProject();

        using (ProjectStore store = temp.Open())
        {
            store.Events.Append(EventSeverity.Info, EventCategory.Dhcp, "as it happened");
        }

        var ex = Assert.Throws<SqliteException>(() => ExecuteRaw(temp.FilePath, "DELETE FROM Event;"));

        Assert.Contains("append-only", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A file written by a later build may carry categories this one has never heard of. Refusing
    /// to open a commissioning record over an unrecognised label would be the worse outcome.
    /// </summary>
    [Fact]
    public void ReadsAnEventFromTheFutureWithoutChoking()
    {
        using var temp = new TempProject();

        using (ProjectStore store = temp.Open())
        {
            store.Events.Append(EventSeverity.Info, EventCategory.Dhcp, "placeholder");
        }

        // Inserted directly: nothing in this build can write a category it does not have.
        ExecuteRaw(
            temp.FilePath,
            "INSERT INTO Event (Utc, Severity, Category, Message) "
                + "VALUES ('2026-08-06T09:00:00.0000000Z', 'info', 'profinet', 'DCP Set Name');");

        using ProjectStore reopened = temp.Open();
        EventRecord row = reopened.Events.All().Single(e => e.Message == "DCP Set Name");

        Assert.Equal(EventCategory.Other, row.Category);
    }

    private static void ExecuteRaw(string path, string sql)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
