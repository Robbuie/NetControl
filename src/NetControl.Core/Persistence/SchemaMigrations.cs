using System.Globalization;
using Microsoft.Data.Sqlite;

namespace NetControl.Core.Persistence;

/// <summary>
/// The schema, as an ordered list of steps rather than a single CREATE script.
///
/// A migration path exists from the first commit on purpose. Opening a project file you created
/// six months ago on a customer site is not a hypothetical - it is the normal case for this tool -
/// and retrofitting migrations onto files that are already in the wild means writing a guesser
/// instead of a migrator.
///
/// Rules for adding a step:
/// <list type="bullet">
///   <item>Append. Never edit a step that has shipped, even to fix a typo in a comment: the file
///         on someone's laptop was built by the old text, and the new text would not reproduce it.</item>
///   <item>Each step must be idempotent-safe under a transaction; the migrator wraps them.</item>
///   <item>Bump <see cref="CurrentVersion"/> to the new count.</item>
/// </list>
/// </summary>
internal static class SchemaMigrations
{
    /// <summary>
    /// Highest schema version this build understands. Always equal to <see cref="Steps"/>.Count -
    /// step index <c>i</c> takes the file from version <c>i</c> to version <c>i + 1</c>.
    /// </summary>
    public const int CurrentVersion = 4;

    public static IReadOnlyList<string> Steps { get; } = [V1, V2, V3, V4];

    /// <summary>
    /// The steps, by index, that rebuild a table other tables point at. SQLite cannot drop a
    /// constraint from a column, so the only way to loosen one is the documented copy-drop-rename,
    /// and that has to run with foreign key enforcement off - dropping the old parent table would
    /// otherwise trip the references to it, even though the new table carries the same ids. The
    /// pragma is a no-op inside a transaction, so <see cref="Migrate"/> sets it around the step
    /// rather than in it, and runs <c>PRAGMA foreign_key_check</c> before committing so that "off"
    /// never means "unchecked".
    /// </summary>
    private static readonly HashSet<int> RebuildsReferencedTable = [3];

    /// <summary>
    /// Initial schema. The table definitions are the ones written out in ROADMAP.md; the triggers
    /// and indexes below are the enforcement the roadmap only stated as a comment.
    /// </summary>
    private const string V1 = """
        CREATE TABLE Project (
            Id            INTEGER PRIMARY KEY,
            Name          TEXT NOT NULL,
            SchemaVersion INTEGER NOT NULL,
            CreatedUtc    TEXT NOT NULL
        );

        CREATE TABLE Device (
            Id          INTEGER PRIMARY KEY,
            Mac         TEXT NOT NULL UNIQUE,
            PlannedIp   TEXT,
            PlannedMask TEXT,
            PlannedGw   TEXT,
            HostName    TEXT,
            PanelRef    TEXT,
            Role        TEXT,
            Vendor      TEXT,
            QuirkFlags  INTEGER NOT NULL DEFAULT 0,
            Notes       TEXT
        );

        CREATE TABLE Assignment (
            Id         INTEGER PRIMARY KEY,
            DeviceId   INTEGER NOT NULL REFERENCES Device(Id),
            ServedIp   TEXT NOT NULL,
            NicIndex   INTEGER NOT NULL,
            NicName    TEXT NOT NULL,
            ServedUtc  TEXT NOT NULL
        );

        CREATE TABLE Event (
            Id        INTEGER PRIMARY KEY,
            Utc       TEXT NOT NULL,
            Severity  TEXT NOT NULL,
            Category  TEXT NOT NULL,
            DeviceId  INTEGER REFERENCES Device(Id),
            Target    TEXT,
            Message   TEXT NOT NULL,
            Detail    TEXT
        );

        CREATE INDEX IX_Event_Utc ON Event(Utc);
        CREATE INDEX IX_Event_Device ON Event(DeviceId);
        CREATE INDEX IX_Assignment_Device ON Assignment(DeviceId);

        -- The event log is the commissioning record. If it can be rewritten it is not a record,
        -- so the ban on UPDATE and DELETE is enforced by the database rather than by everyone
        -- remembering. There is deliberately no API that would trip these; they exist to catch
        -- a future mistake, including one made from a SQL console.
        CREATE TRIGGER TR_Event_NoUpdate BEFORE UPDATE ON Event
        BEGIN
            SELECT RAISE(ABORT, 'The Event table is append-only: rows may not be updated.');
        END;

        CREATE TRIGGER TR_Event_NoDelete BEFORE DELETE ON Event
        BEGIN
            SELECT RAISE(ABORT, 'The Event table is append-only: rows may not be deleted.');
        END;
        """;

    /// <summary>
    /// Scan history (PLAN-TOOLKIT.md F5): every ListIdentity scan and every device that answered it,
    /// so the next scan of the same subnet can say what changed - a device that moved address, a
    /// module swapped for another with a different serial, firmware that changed under a validated
    /// line.
    ///
    /// <para>Append-only, by trigger, for the same reason as <c>Event</c>: "what answered on Tuesday"
    /// is part of the record, and a record that can be edited is not one. The device columns are
    /// the identity object as the device stated it, not anything resolved later - vendor names come
    /// from the IEEE table and move; what the device said does not.</para>
    ///
    /// <para>A file migrated to this version is refused by a build that only knows version 1, with
    /// that build's existing "update the tool" remediation. That is the cost of a table, and the
    /// reason this step waited for a feature that needed one.</para>
    /// </summary>
    private const string V2 = """
        CREATE TABLE ScanRun (
            Id          INTEGER PRIMARY KEY,
            Utc         TEXT NOT NULL,
            NicName     TEXT NOT NULL,
            NicAddress  TEXT,
            NicMask     TEXT,
            Answered    INTEGER NOT NULL
        );

        CREATE TABLE ScanSighting (
            Id           INTEGER PRIMARY KEY,
            ScanRunId    INTEGER NOT NULL REFERENCES ScanRun(Id),
            Address      TEXT NOT NULL,
            Mac          TEXT,
            VendorId     INTEGER NOT NULL,
            DeviceType   INTEGER NOT NULL,
            ProductCode  INTEGER NOT NULL,
            Revision     TEXT NOT NULL,
            Serial       INTEGER NOT NULL,
            ProductName  TEXT NOT NULL
        );

        CREATE INDEX IX_ScanRun_Utc ON ScanRun(Utc);
        CREATE INDEX IX_ScanSighting_Run ON ScanSighting(ScanRunId);

        CREATE TRIGGER TR_ScanRun_NoUpdate BEFORE UPDATE ON ScanRun
        BEGIN
            SELECT RAISE(ABORT, 'The ScanRun table is append-only: rows may not be updated.');
        END;

        CREATE TRIGGER TR_ScanRun_NoDelete BEFORE DELETE ON ScanRun
        BEGIN
            SELECT RAISE(ABORT, 'The ScanRun table is append-only: rows may not be deleted.');
        END;

        CREATE TRIGGER TR_ScanSighting_NoUpdate BEFORE UPDATE ON ScanSighting
        BEGIN
            SELECT RAISE(ABORT, 'The ScanSighting table is append-only: rows may not be updated.');
        END;

        CREATE TRIGGER TR_ScanSighting_NoDelete BEFORE DELETE ON ScanSighting
        BEGIN
            SELECT RAISE(ABORT, 'The ScanSighting table is append-only: rows may not be deleted.');
        END;
        """;

    /// <summary>
    /// Project settings (0.9.0): a key and a value, for what belongs to the project rather than to
    /// the laptop - the DHCP boot options first. Not append-only, unlike the record: a setting is
    /// the current intent, and every change to one is written to <c>Event</c> as it happens, which
    /// is where its history lives.
    /// </summary>
    private const string V3 = """
        CREATE TABLE Setting (
            Key    TEXT PRIMARY KEY,
            Value  TEXT NOT NULL
        );
        """;

    /// <summary>
    /// A plan row may have no MAC yet (PLAN-IMPORT.md F1): imported from the project's IP workbook,
    /// which comes off the drawings, with the MAC filled in later from the BOOTP log or a scan.
    ///
    /// <para><c>Mac</c> loses its <c>NOT NULL</c> and keeps its <c>UNIQUE</c>. SQLite treats every
    /// NULL as distinct, so any number of rows can be awaiting a MAC while a real MAC still appears
    /// once - which is the whole rule, and why no partial index is needed. A MAC-less row is stored
    /// as NULL, never as 00:00:00:00:00:00: the all-zero MAC is a value, and two of them would
    /// collide.</para>
    ///
    /// <para>Ids are copied, not renumbered, because <c>Assignment</c> and the append-only
    /// <c>Event</c> table point at them. The index on <c>PlannedIp</c> is new: a MAC-less row is
    /// matched on re-import by its planned address.</para>
    /// </summary>
    private const string V4 = """
        CREATE TABLE Device_v4 (
            Id          INTEGER PRIMARY KEY,
            Mac         TEXT UNIQUE,
            PlannedIp   TEXT,
            PlannedMask TEXT,
            PlannedGw   TEXT,
            HostName    TEXT,
            PanelRef    TEXT,
            Role        TEXT,
            Vendor      TEXT,
            QuirkFlags  INTEGER NOT NULL DEFAULT 0,
            Notes       TEXT
        );

        INSERT INTO Device_v4 (Id, Mac, PlannedIp, PlannedMask, PlannedGw, HostName, PanelRef, Role, Vendor,
                               QuirkFlags, Notes)
            SELECT Id, Mac, PlannedIp, PlannedMask, PlannedGw, HostName, PanelRef, Role, Vendor, QuirkFlags, Notes
            FROM Device;

        DROP TABLE Device;

        ALTER TABLE Device_v4 RENAME TO Device;

        CREATE INDEX IX_Device_PlannedIp ON Device(PlannedIp);
        """;

    /// <summary>
    /// Brings an open connection up to <see cref="CurrentVersion"/> and returns the version it
    /// arrived at. Each step runs in its own transaction together with the version bump, so an
    /// interrupted migration leaves the file at the last version that completed rather than
    /// halfway between two.
    /// </summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="path">Only used to build error messages.</param>
    public static int Migrate(SqliteConnection connection, string path)
    {
        int version = ReadUserVersion(connection);

        if (version > CurrentVersion)
        {
            throw new SchemaVersionException(path, version, CurrentVersion);
        }

        for (int i = version; i < CurrentVersion; i++)
        {
            bool rebuild = RebuildsReferencedTable.Contains(i);
            bool enforcing = rebuild && ReadForeignKeys(connection);

            if (enforcing)
            {
                Execute(connection, "PRAGMA foreign_keys = OFF;");
            }

            try
            {
                using SqliteTransaction transaction = connection.BeginTransaction();

                Execute(connection, Steps[i]);

                if (rebuild)
                {
                    RequireNoDanglingReferences(connection, path, i + 1);
                }

                // PRAGMA does not take parameters, hence the interpolation. The value is a loop
                // counter, not input.
                Execute(connection, $"PRAGMA user_version = {i + 1};");

                transaction.Commit();
            }
            finally
            {
                if (enforcing)
                {
                    Execute(connection, "PRAGMA foreign_keys = ON;");
                }
            }
        }

        return CurrentVersion;
    }

    public static int ReadUserVersion(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static bool ReadForeignKeys(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys;";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    /// <summary>
    /// The check the database would have made had enforcement been on. Throwing here rolls the step
    /// back, so a file that fails it is left at the version it arrived at, untouched.
    /// </summary>
    private static void RequireNoDanglingReferences(SqliteConnection connection, string path, int toVersion)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check;";

        using SqliteDataReader reader = command.ExecuteReader();
        if (reader.Read())
        {
            throw new PersistenceException(
                $"Could not upgrade '{path}' to schema version {toVersion}: the {reader.GetString(0)} table "
                + $"refers to a {reader.GetString(2)} row that does not exist.")
            {
                Remediation = "The file has not been changed. It was most likely edited outside this tool - "
                    + "open it in the version that wrote it, or send it in with the diagnostic log.",
            };
        }
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        // SqliteConnection.CreateCommand() copies the connection's pending transaction onto the
        // command. That matters: Microsoft.Data.Sqlite throws if a command is executed while the
        // connection has a transaction the command does not know about. Always create commands
        // from the connection, never with `new SqliteCommand(sql, connection)`.
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
