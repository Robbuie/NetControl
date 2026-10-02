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
    public const int CurrentVersion = 2;

    public static IReadOnlyList<string> Steps { get; } = [V1, V2];

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
            using SqliteTransaction transaction = connection.BeginTransaction();

            Execute(connection, Steps[i]);

            // PRAGMA does not take parameters, hence the interpolation. The value is a loop
            // counter, not input.
            Execute(connection, $"PRAGMA user_version = {i + 1};");

            transaction.Commit();
        }

        return CurrentVersion;
    }

    public static int ReadUserVersion(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
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
