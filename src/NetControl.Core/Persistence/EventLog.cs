using System.Globalization;
using Microsoft.Data.Sqlite;

namespace NetControl.Core.Persistence;

/// <summary>
/// The append-only commissioning record.
///
/// Every state-changing operation this tool performs writes a row here: timestamp, target, what
/// was sent, what came back. That is the rule the whole product rests on, and it is why this class
/// has no Update and no Delete - not as a convention, but because the database refuses them too
/// (see the triggers in <see cref="SchemaMigrations"/>). If a row could be quietly changed, the
/// log would be a story rather than a record, and the report handed to a customer at the end of a
/// startup would be worth nothing.
///
/// This is not the diagnostic log. Serilog writes a rolling file for working out what the *tool*
/// did; this table is for what the *devices* did.
/// </summary>
public sealed class EventLog
{
    private const string SelectColumns =
        "SELECT Id, Utc, Severity, Category, DeviceId, Target, Message, Detail FROM Event";

    private const string SelectRecent = SelectColumns + " ORDER BY Utc DESC, Id DESC LIMIT $limit;";

    private const string SelectForDevice = SelectColumns + " WHERE DeviceId = $id ORDER BY Utc, Id;";

    private const string SelectSince = SelectColumns + " WHERE Utc >= $utc ORDER BY Utc, Id;";

    private const string SelectAll = SelectColumns + " ORDER BY Utc, Id;";

    private readonly ProjectStore _store;

    internal EventLog(ProjectStore store) => _store = store;

    public int Count => _store.Read(connection =>
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Event;";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    });

    /// <summary>Writes one row and returns its id.</summary>
    public long Append(EventRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return _store.InTransaction(connection =>
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO Event (Utc, Severity, Category, DeviceId, Target, Message, Detail)
                VALUES ($utc, $severity, $category, $device, $target, $message, $detail)
                RETURNING Id;
                """;

            command.Parameters.AddWithValue("$utc", SqlTime.ToSql(record.Utc));
            command.Parameters.AddWithValue("$severity", SqlRead.EnumText(record.Severity));
            command.Parameters.AddWithValue("$category", SqlRead.EnumText(record.Category));
            command.Parameters.AddWithValue("$device", (object?)record.DeviceId ?? DBNull.Value);
            command.Parameters.AddWithValue("$target", (object?)record.Target ?? DBNull.Value);
            command.Parameters.AddWithValue("$message", record.Message);
            command.Parameters.AddWithValue("$detail", (object?)record.Detail ?? DBNull.Value);

            return (long)command.ExecuteScalar()!;
        });
    }

    /// <summary>
    /// Convenience for the common case. Timestamps from the store's clock, so a test driving a
    /// fake clock gets the times it expects.
    /// </summary>
    public long Append(
        EventSeverity severity,
        EventCategory category,
        string message,
        string? target = null,
        long? deviceId = null,
        EventDetail? detail = null) =>
        Append(new EventRecord
        {
            Utc = _store.Clock.GetUtcNow(),
            Severity = severity,
            Category = category,
            Message = message,
            Target = target,
            DeviceId = deviceId,
            Detail = detail?.ToJson(),
        });

    /// <summary>Newest first, for the live view. Bounded because a long session runs to thousands of rows.</summary>
    public IReadOnlyList<EventRecord> Recent(int limit = 500) => _store.Read(connection =>
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = SelectRecent;
        command.Parameters.AddWithValue("$limit", Math.Max(1, limit));
        return ReadAll(command);
    });

    /// <summary>Oldest first - this is the order a commissioning report reads in.</summary>
    public IReadOnlyList<EventRecord> ForDevice(long deviceId) => _store.Read(connection =>
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = SelectForDevice;
        command.Parameters.AddWithValue("$id", deviceId);
        return ReadAll(command);
    });

    /// <summary>Oldest first, from a point in time. This is how a session's slice of the log is pulled out.</summary>
    public IReadOnlyList<EventRecord> Since(DateTimeOffset utc) => _store.Read(connection =>
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = SelectSince;

        // Text comparison, which is chronological only because SqlTime writes a fixed-width
        // round-trip format. See SqlTime for why that matters.
        command.Parameters.AddWithValue("$utc", SqlTime.ToSql(utc));
        return ReadAll(command);
    });

    /// <summary>The whole record, oldest first. What the Phase 3 report renders.</summary>
    public IReadOnlyList<EventRecord> All() => _store.Read(connection =>
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = SelectAll;
        return ReadAll(command);
    });

    private static IReadOnlyList<EventRecord> ReadAll(SqliteCommand command)
    {
        using SqliteDataReader reader = command.ExecuteReader();
        List<EventRecord> rows = [];

        while (reader.Read())
        {
            rows.Add(new EventRecord
            {
                Id = reader.GetInt64(0),
                Utc = SqlTime.FromSql(reader.GetString(1)),
                Severity = SqlRead.EnumValue(reader, 2, EventSeverity.Info),
                Category = SqlRead.EnumValue(reader, 3, EventCategory.Other),
                DeviceId = SqlRead.NullableInt64(reader, 4),
                Target = SqlRead.NullableString(reader, 5),
                Message = reader.GetString(6),
                Detail = SqlRead.NullableString(reader, 7),
            });
        }

        return rows;
    }
}
