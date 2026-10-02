using System.Globalization;
using System.Net;
using Microsoft.Data.Sqlite;

namespace NetControl.Core.Persistence;

/// <summary>
/// Every scan this project has run and every device that answered each one.
///
/// <para>No Update and no Delete, and the database enforces it with triggers as it does for the
/// event log: what answered a scan on a given day is part of the record of what was on that network,
/// and a history that can be edited is not one.</para>
/// </summary>
public sealed class ScanHistory
{
    private const string SelectRuns =
        "SELECT Id, Utc, NicName, NicAddress, NicMask, Answered FROM ScanRun ORDER BY Utc DESC, Id DESC;";

    private const string SelectSightings = """
        SELECT Id, ScanRunId, Address, Mac, VendorId, DeviceType, ProductCode, Revision, Serial, ProductName
        FROM ScanSighting
        WHERE ScanRunId = $run
        ORDER BY Id;
        """;

    private readonly ProjectStore _store;

    internal ScanHistory(ProjectStore store) => _store = store;

    public int Count => _store.Read(connection =>
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM ScanRun;";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    });

    /// <summary>Records one scan and everything that answered it, in one transaction.</summary>
    /// <returns>The new run's id.</returns>
    public long Record(
        DateTimeOffset utc,
        string nicName,
        IPAddress? nicAddress,
        IPAddress? nicMask,
        IReadOnlyList<ScanSightingRecord> sightings)
    {
        ArgumentNullException.ThrowIfNull(sightings);

        return _store.InTransaction(connection =>
        {
            long runId;

            using (SqliteCommand run = connection.CreateCommand())
            {
                run.CommandText = """
                    INSERT INTO ScanRun (Utc, NicName, NicAddress, NicMask, Answered)
                    VALUES ($utc, $name, $address, $mask, $answered)
                    RETURNING Id;
                    """;
                run.Parameters.AddWithValue("$utc", SqlTime.ToSql(utc));
                run.Parameters.AddWithValue("$name", nicName ?? string.Empty);
                run.Parameters.AddWithValue("$address", (object?)nicAddress?.ToString() ?? DBNull.Value);
                run.Parameters.AddWithValue("$mask", (object?)nicMask?.ToString() ?? DBNull.Value);
                run.Parameters.AddWithValue("$answered", sightings.Count);
                runId = (long)run.ExecuteScalar()!;
            }

            foreach (ScanSightingRecord sighting in sightings)
            {
                using SqliteCommand insert = connection.CreateCommand();
                insert.CommandText = """
                    INSERT INTO ScanSighting
                        (ScanRunId, Address, Mac, VendorId, DeviceType, ProductCode, Revision, Serial, ProductName)
                    VALUES ($run, $address, $mac, $vendor, $type, $product, $revision, $serial, $name);
                    """;
                insert.Parameters.AddWithValue("$run", runId);
                insert.Parameters.AddWithValue("$address", sighting.Address.ToString());
                insert.Parameters.AddWithValue("$mac", sighting.Mac.IsEmpty ? DBNull.Value : (object)sighting.Mac.ToString());
                insert.Parameters.AddWithValue("$vendor", (int)sighting.VendorId);
                insert.Parameters.AddWithValue("$type", (int)sighting.DeviceType);
                insert.Parameters.AddWithValue("$product", (int)sighting.ProductCode);
                insert.Parameters.AddWithValue("$revision", sighting.Revision);
                insert.Parameters.AddWithValue("$serial", (long)sighting.Serial);
                insert.Parameters.AddWithValue("$name", sighting.ProductName);
                insert.ExecuteNonQuery();
            }

            return runId;
        });
    }

    /// <summary>Every run, newest first.</summary>
    public IReadOnlyList<ScanRunRecord> Runs() => _store.Read(connection =>
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = SelectRuns;

        using SqliteDataReader reader = command.ExecuteReader();
        List<ScanRunRecord> runs = [];

        while (reader.Read())
        {
            runs.Add(new ScanRunRecord(
                reader.GetInt64(0),
                SqlTime.FromSql(reader.GetString(1)),
                reader.GetString(2),
                SqlRead.Address(reader, 3),
                SqlRead.Address(reader, 4),
                reader.GetInt32(5)));
        }

        return (IReadOnlyList<ScanRunRecord>)runs;
    });

    /// <summary>
    /// The most recent scan of the same subnet as <paramref name="subnet"/>, or null when there has
    /// not been one. A scan of a different segment is not a "before" for this one, however recent.
    /// </summary>
    public ScanRunRecord? LatestOn(Ipv4Subnet subnet) =>
        Runs().FirstOrDefault(run => run.TryGetSubnet(out Ipv4Subnet other)
            && other.PrefixLength == subnet.PrefixLength
            && other.Network.Equals(subnet.Network));

    public IReadOnlyList<ScanSightingRecord> Sightings(long runId) => _store.Read(connection =>
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = SelectSightings;
        command.Parameters.AddWithValue("$run", runId);

        using SqliteDataReader reader = command.ExecuteReader();
        List<ScanSightingRecord> sightings = [];

        while (reader.Read())
        {
            string? mac = SqlRead.NullableString(reader, 3);

            sightings.Add(new ScanSightingRecord
            {
                Id = reader.GetInt64(0),
                ScanRunId = reader.GetInt64(1),
                Address = SqlRead.Address(reader, 2) ?? IPAddress.Any,
                Mac = MacAddress.TryParse(mac, out MacAddress parsed) ? parsed : MacAddress.Empty,
                VendorId = (ushort)reader.GetInt32(4),
                DeviceType = (ushort)reader.GetInt32(5),
                ProductCode = (ushort)reader.GetInt32(6),
                Revision = reader.GetString(7),
                Serial = (uint)reader.GetInt64(8),
                ProductName = reader.GetString(9),
            });
        }

        return (IReadOnlyList<ScanSightingRecord>)sightings;
    });
}
