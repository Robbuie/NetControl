using System.Globalization;
using System.Net;
using Microsoft.Data.Sqlite;

namespace NetControl.Core.Persistence;

/// <summary>
/// What was actually served, to whom, over which adapter.
///
/// There is no Update and no Delete here either. It is not enforced by a trigger the way the event
/// log is - an <c>Assignment</c> row is a summary that could in principle be rebuilt from the log -
/// but the absence of the methods is deliberate: nothing in this product has a reason to rewrite
/// history, and an API that cannot do it is easier to trust than a rule people have to remember.
/// </summary>
public sealed class AssignmentRepository
{
    private const string SelectColumns =
        "SELECT Id, DeviceId, ServedIp, NicIndex, NicName, ServedUtc FROM Assignment";

    private const string SelectForDevice = SelectColumns + " WHERE DeviceId = $id ORDER BY ServedUtc DESC, Id DESC;";

    private const string SelectRecent = SelectColumns + " ORDER BY ServedUtc DESC, Id DESC LIMIT $limit;";

    private readonly ProjectStore _store;

    internal AssignmentRepository(ProjectStore store) => _store = store;

    public int Count => _store.Read(connection =>
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Assignment;";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    });

    /// <summary>
    /// Records one served address.
    /// </summary>
    /// <param name="deviceId">Must exist - the foreign key is on, and an assignment to nobody is meaningless.</param>
    /// <param name="servedIp">The address in the reply.</param>
    /// <param name="nicIndex">Interface index the reply left by.</param>
    /// <param name="nicName">Adapter name at the time. Interface indexes get reused, names are what a human recognises.</param>
    /// <param name="servedUtc">When it hit the wire.</param>
    /// <returns>The new row id.</returns>
    public long Record(long deviceId, IPAddress servedIp, int nicIndex, string nicName, DateTimeOffset servedUtc)
    {
        ArgumentNullException.ThrowIfNull(servedIp);

        return _store.InTransaction(connection =>
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO Assignment (DeviceId, ServedIp, NicIndex, NicName, ServedUtc)
                VALUES ($device, $ip, $index, $name, $utc)
                RETURNING Id;
                """;
            command.Parameters.AddWithValue("$device", deviceId);
            command.Parameters.AddWithValue("$ip", servedIp.ToString());
            command.Parameters.AddWithValue("$index", nicIndex);
            command.Parameters.AddWithValue("$name", nicName ?? string.Empty);
            command.Parameters.AddWithValue("$utc", SqlTime.ToSql(servedUtc));

            return (long)command.ExecuteScalar()!;
        });
    }

    /// <summary>Most recent first. A device served more than once is worth seeing as more than once.</summary>
    public IReadOnlyList<AssignmentRecord> ForDevice(long deviceId) => _store.Read(connection =>
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = SelectForDevice;
        command.Parameters.AddWithValue("$id", deviceId);
        return ReadAll(command);
    });

    /// <summary>
    /// The ids of every device this project has ever served, as a set.
    ///
    /// One query rather than a <see cref="ForDevice"/> per row: the grid asks this to grade a whole
    /// plan at once, and a plan is hundreds of rows on a big line.
    /// </summary>
    public IReadOnlySet<long> ServedDeviceIds() => _store.Read(connection =>
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT DeviceId FROM Assignment;";

        using SqliteDataReader reader = command.ExecuteReader();
        HashSet<long> ids = [];
        while (reader.Read())
        {
            ids.Add(reader.GetInt64(0));
        }

        return (IReadOnlySet<long>)ids;
    });

    public IReadOnlyList<AssignmentRecord> Recent(int limit = 200) => _store.Read(connection =>
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = SelectRecent;
        command.Parameters.AddWithValue("$limit", Math.Max(1, limit));
        return ReadAll(command);
    });

    private static IReadOnlyList<AssignmentRecord> ReadAll(SqliteCommand command)
    {
        using SqliteDataReader reader = command.ExecuteReader();
        List<AssignmentRecord> rows = [];

        while (reader.Read())
        {
            rows.Add(new AssignmentRecord(
                reader.GetInt64(0),
                reader.GetInt64(1),
                SqlRead.Address(reader, 2) ?? IPAddress.Any,
                reader.GetInt32(3),
                reader.GetString(4),
                SqlTime.FromSql(reader.GetString(5))));
        }

        return rows;
    }
}
