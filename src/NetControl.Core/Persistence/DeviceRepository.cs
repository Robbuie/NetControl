using System.Globalization;
using Microsoft.Data.Sqlite;
using NetControl.Core.Dhcp;

namespace NetControl.Core.Persistence;

/// <summary>
/// The plan, stored. Reads and writes <c>Device</c> rows, keyed on MAC - or, for a row still
/// awaiting its MAC (PLAN-IMPORT.md F1), on its planned address.
///
/// Everything here is synchronous. SQLite on a local file is a memcpy with extra steps, and
/// Microsoft.Data.Sqlite's async methods are synchronous underneath anyway - an async signature
/// here would promise something the storage engine does not do. A UI that needs to import a
/// thousand rows without blocking wraps the call, and gets one <see cref="UpsertRange"/>
/// transaction rather than a thousand awaits.
/// </summary>
public sealed class DeviceRepository
{
    // Built from constants rather than interpolated at the call site: the column list is shared,
    // but every statement that reaches the database is still a compile-time constant with
    // parameters for the values. Nothing user-supplied is ever concatenated into SQL.
    private const string SelectColumns =
        "SELECT Id, Mac, PlannedIp, PlannedMask, PlannedGw, HostName, PanelRef, Role, Vendor, QuirkFlags, Notes "
        + "FROM Device";

    private const string SelectByMac = SelectColumns + " WHERE Mac = $mac;";

    private const string SelectById = SelectColumns + " WHERE Id = $id;";

    private const string SelectAll = SelectColumns + " ORDER BY PlannedIp IS NULL, PlannedIp, Mac;";

    private const string SelectByPlannedIp = SelectColumns + " WHERE PlannedIp = $ip ORDER BY Id;";

    private readonly ProjectStore _store;

    internal DeviceRepository(ProjectStore store) => _store = store;

    public int Count => _store.Read(connection =>
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Device;";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    });

    /// <summary>
    /// Inserts, or replaces the row this device already has. Replaces rather than merges: a
    /// re-import of a corrected CSV must be able to blank a field that should not be set, and a
    /// merge would leave yesterday's wrong gateway sitting in the plan with nothing to show it was
    /// ever there.
    ///
    /// <para><b>Except the quirks, which are merged.</b> A CSV has no quirks column, so a re-import
    /// would otherwise zero what Set static learned about every device in the file. They are cleared
    /// by a person in the quirk editor, which goes through <see cref="Update"/> and replaces.</para>
    ///
    /// <para><b>Which row is "already there"</b> depends on whether the device has a MAC:</para>
    /// <list type="bullet">
    ///   <item>With a MAC, the row holding that MAC. Failing that, a row <em>awaiting</em> a MAC at
    ///         the same planned address takes it - a CSV with MACs, imported over the workbook that
    ///         had none, fills the plan in rather than doubling it.</item>
    ///   <item>Without one, the row at the same planned address, whose MAC (and the vendor that came
    ///         with it) is kept: re-importing the workbook must not throw away a MAC that was filled
    ///         in from the wire. Two rows at that address is ambiguous and refused, rather than one
    ///         of them winning silently.</item>
    /// </list>
    /// </summary>
    /// <returns>The row id, whether inserted or updated.</returns>
    /// <exception cref="PersistenceException">
    /// The MAC cannot identify a device, a MAC-less row has no planned address to be matched by, or
    /// more than one row already holds that address.
    /// </exception>
    public long Upsert(DeviceRecord device)
    {
        ArgumentNullException.ThrowIfNull(device);

        Guard(device);

        return _store.InTransaction(connection =>
        {
            using SqliteCommand command = connection.CreateCommand();

            if (device.Mac.IsEmpty)
            {
                if (RowAtAddress(device.PlannedIp, awaitingOnly: false) is { } id)
                {
                    return UpdateKeepingMac(connection, id, device);
                }
            }
            else if (Find(device.Mac) is null && RowAtAddress(device.PlannedIp, awaitingOnly: true) is { } awaiting)
            {
                // The MAC is being filled in on a row that was waiting for it.
                command.CommandText = """
                    UPDATE Device SET
                        Mac         = $mac,
                        PlannedIp   = $ip,
                        PlannedMask = $mask,
                        PlannedGw   = $gw,
                        HostName    = $host,
                        PanelRef    = $panel,
                        Role        = $role,
                        Vendor      = $vendor,
                        QuirkFlags  = $quirks | QuirkFlags,
                        Notes       = $notes
                    WHERE Id = $id
                    RETURNING Id;
                    """;
                command.Parameters.AddWithValue("$id", awaiting);
                AddValues(command, device);
                return (long)command.ExecuteScalar()!;
            }

            // A new row, or the row holding this MAC. A NULL MAC never conflicts, so a device
            // with none always lands here as a new row.
            command.CommandText = """
                INSERT INTO Device (Mac, PlannedIp, PlannedMask, PlannedGw, HostName, PanelRef, Role, Vendor,
                                    QuirkFlags, Notes)
                VALUES ($mac, $ip, $mask, $gw, $host, $panel, $role, $vendor, $quirks, $notes)
                ON CONFLICT(Mac) DO UPDATE SET
                    PlannedIp   = excluded.PlannedIp,
                    PlannedMask = excluded.PlannedMask,
                    PlannedGw   = excluded.PlannedGw,
                    HostName    = excluded.HostName,
                    PanelRef    = excluded.PanelRef,
                    Role        = excluded.Role,
                    Vendor      = excluded.Vendor,
                    QuirkFlags  = excluded.QuirkFlags | Device.QuirkFlags,
                    Notes       = excluded.Notes
                RETURNING Id;
                """;

            AddValues(command, device);

            return (long)command.ExecuteScalar()!;
        });
    }

    /// <summary>
    /// Imports a whole plan in one transaction. All or nothing on purpose: a CSV that fails
    /// halfway through leaves the project holding half a plan, and half a plan is worse than none
    /// because it looks complete.
    /// </summary>
    /// <returns>How many rows were written.</returns>
    public int UpsertRange(IEnumerable<DeviceRecord> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);

        List<DeviceRecord> batch = [.. devices];

        return _store.InTransaction(_ =>
        {
            foreach (DeviceRecord device in batch)
            {
                Upsert(device);
            }

            return batch.Count;
        });
    }

    /// <summary>
    /// Edits the row with <see cref="DeviceRecord.Id"/>, MAC included.
    ///
    /// <para>Distinct from <see cref="Upsert(DeviceRecord)"/>, which is keyed on MAC and would
    /// therefore turn "fix the typo in this MAC" into a second row beside the wrong one. The grid
    /// edits a row that already exists, so it needs to say which row.</para>
    ///
    /// <para>Changing the MAC of a device that already appears in the commissioning record is
    /// refused for the same reason <see cref="Delete(long)"/> is: the <c>Event</c> table is append-only
    /// and its rows point at this Id, so renaming it would rewrite what those events say happened.</para>
    /// </summary>
    /// <returns>False if the row is gone - deleted underneath an open grid.</returns>
    public bool Update(DeviceRecord device)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (device.Id == 0)
        {
            throw new ArgumentException(
                "Update needs the row id. Use Upsert for a device that is not in the plan yet.", nameof(device));
        }

        Guard(device);

        return _store.InTransaction(connection =>
        {
            DeviceRecord? existing = Get(device.Id);
            if (existing is null)
            {
                return false;
            }

            if (existing.Mac != device.Mac)
            {
                // Filling in the MAC of a row that was awaiting one is not renaming a device: the
                // record never named a MAC for it, so nothing it says is rewritten.
                if (!existing.Mac.IsEmpty && ReferenceCount(connection, device.Id) > 0)
                {
                    throw new PersistenceException(
                        $"{existing.Mac} already appears in this project's commissioning record, so its MAC "
                        + "cannot be changed.")
                    {
                        Remediation = $"Add {device.Mac} as a new row instead. The record has to keep saying what "
                            + "actually happened.",
                    };
                }

                if (!device.Mac.IsEmpty && Find(device.Mac) is { } clash)
                {
                    throw new PersistenceException($"{device.Mac} is already in the plan (row {clash.Id}).")
                    {
                        Remediation = "Two rows cannot share a MAC - the plan is keyed on it. Edit the existing "
                            + "row, or remove it first.",
                    };
                }
            }

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                UPDATE Device SET
                    Mac         = $mac,
                    PlannedIp   = $ip,
                    PlannedMask = $mask,
                    PlannedGw   = $gw,
                    HostName    = $host,
                    PanelRef    = $panel,
                    Role        = $role,
                    Vendor      = $vendor,
                    QuirkFlags  = $quirks,
                    Notes       = $notes
                WHERE Id = $id;
                """;

            command.Parameters.AddWithValue("$id", device.Id);
            AddValues(command, device);

            return command.ExecuteNonQuery() > 0;
        });
    }

    /// <summary>The row holding this MAC. Never a row awaiting one: the empty MAC finds nothing.</summary>
    public DeviceRecord? Find(MacAddress mac) => mac.IsEmpty ? null : _store.Read(connection =>
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = SelectByMac;
        command.Parameters.AddWithValue("$mac", mac.ToString());

        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    });

    public DeviceRecord? Get(long id) => _store.Read(connection =>
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = SelectById;
        command.Parameters.AddWithValue("$id", id);

        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    });

    /// <summary>Ordered by planned address where there is one, then by MAC, so the grid is stable.</summary>
    public IReadOnlyList<DeviceRecord> All() => _store.Read(connection =>
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = SelectAll;

        using SqliteDataReader reader = command.ExecuteReader();
        List<DeviceRecord> devices = [];
        while (reader.Read())
        {
            devices.Add(Map(reader));
        }

        return (IReadOnlyList<DeviceRecord>)devices;
    });

    /// <summary>
    /// Removes a planned device. Refuses once the device appears in the commissioning record:
    /// the <c>Event</c> table is append-only, so a row it points at cannot be made to vanish
    /// without leaving the record referring to a device that never existed.
    /// </summary>
    /// <returns>False if there was no such MAC.</returns>
    public bool Delete(MacAddress mac) => Find(mac) is { } device && Delete(device.Id);

    /// <summary>
    /// Removes a planned device by row id - the only way to name a row that is still awaiting its
    /// MAC. Same refusal as <see cref="Delete(MacAddress)"/>.
    /// </summary>
    /// <returns>False if there was no such row.</returns>
    public bool Delete(long id)
    {
        DeviceRecord? device = Get(id);
        if (device is null)
        {
            return false;
        }

        return _store.InTransaction(connection =>
        {
            if (ReferenceCount(connection, device.Id) > 0)
            {
                throw new PersistenceException(
                    $"{Describe(device)} already appears in this project's commissioning record, so it cannot be "
                    + "removed.")
                {
                    Remediation = "Edit the row instead, or start a new project file if the plan was wrong "
                        + "from the beginning.",
                };
            }

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DELETE FROM Device WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", device.Id);
            return command.ExecuteNonQuery() > 0;
        });
    }

    /// <summary>
    /// The plan as the DHCP engine wants it, plus a list of the rows that could not be turned into
    /// one and why.
    ///
    /// Both halves matter. The assignments go into <see cref="StaticMapPolicy"/>; the problems go
    /// on screen <em>before</em> serve mode starts, because "3 of 14 planned devices will not be
    /// served" is something to find out at the desk, not by watching a panel stay dark.
    /// </summary>
    public IReadOnlyList<DeviceAssignment> PlannedAssignments(out IReadOnlyList<string> problems)
    {
        List<DeviceAssignment> assignments = [];
        List<string> found = [];

        foreach (DeviceRecord device in All())
        {
            if (device.TryToAssignment(out DeviceAssignment? assignment, out string? problem))
            {
                assignments.Add(assignment!);
            }
            else if (device.IsPlanned && !device.IsAwaitingMac)
            {
                // A row with no address at all is not a problem, it is a plan in progress - and so
                // is a row awaiting its MAC. A row with an address that will not validate is a
                // problem.
                found.Add(problem!);
            }
        }

        problems = found;
        return assignments;
    }

    private static DeviceRecord Map(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        Mac = !reader.IsDBNull(1) && MacAddress.TryParse(reader.GetString(1), out MacAddress mac) ? mac : MacAddress.Empty,
        PlannedIp = SqlRead.Address(reader, 2),
        PlannedMask = SqlRead.Address(reader, 3),
        PlannedGateway = SqlRead.Address(reader, 4),
        HostName = SqlRead.NullableString(reader, 5),
        PanelRef = SqlRead.NullableString(reader, 6),
        Role = SqlRead.NullableString(reader, 7),
        Vendor = SqlRead.NullableString(reader, 8),
        Quirks = (DeviceQuirks)reader.GetInt64(9),
        Notes = SqlRead.NullableString(reader, 10),
    };

    /// <summary>
    /// A MAC that cannot identify one device cannot go in the plan, whichever way in it came.
    /// Serve mode answers exactly the MACs listed here, so a broadcast or multicast address in
    /// this table is a rogue reply waiting to happen.
    ///
    /// <para>No MAC at all is allowed - the row is awaiting one, and is never served - but only
    /// with a planned address. That address is all such a row can be found by, on re-import and
    /// when its MAC is filled in from the wire; without it the row would be a name nothing could
    /// ever be matched to.</para>
    /// </summary>
    private static void Guard(DeviceRecord device)
    {
        MacAddress mac = device.Mac;

        if (mac.IsEmpty)
        {
            if (device.PlannedIp is not null)
            {
                return;
            }

            throw new PersistenceException(
                $"{device.DisplayName ?? "This row"} has neither a MAC nor a planned address, so nothing could "
                + "ever match it to a device.")
            {
                Remediation = "Give it a planned address from the drawing, or the MAC off the device label.",
            };
        }

        if (!mac.IsBroadcast && !mac.IsMulticast)
        {
            return;
        }

        throw new PersistenceException($"'{mac}' cannot identify a device, so it cannot go in the plan.")
        {
            Remediation = "Check the MAC on the device label - it should be six hex bytes, "
                + "e.g. 00:1D:9C:C7:B0:70.",
        };
    }

    /// <summary>
    /// The row at a planned address, for matching a device that has no MAC to be matched by. Null
    /// for no address or no row; refused when more than one row holds it, because picking one
    /// would be the import deciding which device somebody meant.
    /// </summary>
    private long? RowAtAddress(System.Net.IPAddress? address, bool awaitingOnly)
    {
        if (address is null)
        {
            return null;
        }

        List<DeviceRecord> at = _store.Read(connection =>
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = SelectByPlannedIp;
            command.Parameters.AddWithValue("$ip", address.ToString());

            using SqliteDataReader reader = command.ExecuteReader();
            List<DeviceRecord> rows = [];
            while (reader.Read())
            {
                rows.Add(Map(reader));
            }

            return rows;
        });

        if (awaitingOnly)
        {
            at.RemoveAll(row => !row.IsAwaitingMac);
        }

        return at.Count switch
        {
            0 => null,
            1 => at[0].Id,
            _ => throw new PersistenceException(
                $"{at.Count} rows in the plan are already planned for {address} ("
                + string.Join(", ", at.Select(Describe)) + "), so there is no telling which one this is.")
            {
                Remediation = "Correct or remove the extra rows so the address is planned once, then import again.",
            },
        };
    }

    /// <summary>
    /// Replaces a row's planned fields from a device that has no MAC, keeping the MAC and vendor the
    /// row already has and merging the quirks - see <see cref="Upsert"/>.
    /// </summary>
    private static long UpdateKeepingMac(SqliteConnection connection, long id, DeviceRecord device)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Device SET
                PlannedIp   = $ip,
                PlannedMask = $mask,
                PlannedGw   = $gw,
                HostName    = $host,
                PanelRef    = $panel,
                Role        = $role,
                QuirkFlags  = $quirks | QuirkFlags,
                Notes       = $notes
            WHERE Id = $id
            RETURNING Id;
            """;

        command.Parameters.AddWithValue("$id", id);
        AddValues(command, device);

        return (long)command.ExecuteScalar()!;
    }

    /// <summary>A row as a message names it: its label and MAC, or that it is still awaiting one.</summary>
    private static string Describe(DeviceRecord device)
    {
        string mac = device.IsAwaitingMac ? "awaiting MAC" : device.Mac.ToString();
        return device.DisplayName is { } name ? $"{name} ({mac})" : mac;
    }

    /// <summary>
    /// How many rows of the commissioning record point at this device. Non-zero means the row is
    /// part of what happened and can no longer be renamed or removed.
    /// </summary>
    private static long ReferenceCount(SqliteConnection connection, long deviceId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT (SELECT COUNT(*) FROM Assignment WHERE DeviceId = $id)
                 + (SELECT COUNT(*) FROM Event WHERE DeviceId = $id);
            """;
        command.Parameters.AddWithValue("$id", deviceId);
        return (long)command.ExecuteScalar()!;
    }

    /// <summary>
    /// The value parameters, shared by the insert and the update so the two cannot drift. The Id
    /// is not among them: one statement does not have it yet and the other keys on it.
    /// </summary>
    private static void AddValues(SqliteCommand command, DeviceRecord device)
    {
        AddNullable(command, "$mac", device.Mac.IsEmpty ? null : device.Mac.ToString());
        AddNullable(command, "$ip", device.PlannedIp?.ToString());
        AddNullable(command, "$mask", device.PlannedMask?.ToString());
        AddNullable(command, "$gw", device.PlannedGateway?.ToString());
        AddNullable(command, "$host", Trimmed(device.HostName));
        AddNullable(command, "$panel", Trimmed(device.PanelRef));
        AddNullable(command, "$role", Trimmed(device.Role));
        AddNullable(command, "$vendor", Trimmed(device.Vendor));
        command.Parameters.AddWithValue("$quirks", (long)device.Quirks);
        AddNullable(command, "$notes", Trimmed(device.Notes));
    }

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void AddNullable(SqliteCommand command, string name, string? value) =>
        command.Parameters.AddWithValue(name, (object?)value ?? DBNull.Value);
}
