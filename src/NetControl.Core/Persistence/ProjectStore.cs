using System.Globalization;
using Microsoft.Data.Sqlite;

namespace NetControl.Core.Persistence;

/// <summary>
/// One project file: the plan, what was served, and the event log. Owns the SQLite connection and
/// hands out the repositories that read and write through it.
///
/// <para><b>Threading.</b> There is exactly one connection and one lock. SQLite connections are
/// not thread-safe, and this store is written to from the DHCP receive loop while a UI thread
/// reads from it, so every operation takes the lock. That serialises the database, which is the
/// right trade here: the writes are tiny and infrequent (a handful per device), and a connection
/// pool would buy throughput nobody needs in exchange for a class of bug that is very hard to
/// reproduce on a bench.</para>
///
/// <para><b>Journal mode.</b> Left at SQLite's default rollback journal rather than WAL. WAL is
/// faster under concurrent readers, which the lock above means we do not have, and it leaves
/// <c>-wal</c> and <c>-shm</c> sidecar files next to the project. A project file gets copied to a
/// USB stick and emailed to the next engineer; a copy that silently loses the last few commits
/// because the sidecar was left behind would be a genuinely bad failure.</para>
/// </summary>
public sealed class ProjectStore : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly Lock _gate = new();
    private int _transactionDepth;
    private bool _disposed;

    private ProjectStore(SqliteConnection connection, string filePath, bool isInMemory, TimeProvider time)
    {
        _connection = connection;
        Clock = time;
        FilePath = filePath;
        IsInMemory = isInMemory;

        Devices = new DeviceRepository(this);
        Assignments = new AssignmentRepository(this);
        Events = new EventLog(this);
        Scans = new ScanHistory(this);
        Settings = new ProjectSettings(this);
        Project = LoadProject();
    }

    /// <summary>
    /// The clock everything written through this store timestamps with. Shared so that a test
    /// driving a fake clock gets consistent timestamps across the event log and the recorder.
    /// </summary>
    internal TimeProvider Clock { get; }

    /// <summary>The file on disk, or a placeholder name for an in-memory store.</summary>
    public string FilePath { get; }

    /// <summary>True when nothing is being written to disk. See <see cref="CreateInMemory"/>.</summary>
    public bool IsInMemory { get; }

    /// <summary>The single Project row, refreshed whenever it changes.</summary>
    public ProjectInfo Project { get; private set; }

    public DeviceRepository Devices { get; }

    public AssignmentRepository Assignments { get; }

    public EventLog Events { get; }

    /// <summary>Every scan and what answered it. Schema version 2; see PLAN-TOOLKIT.md F5.</summary>
    public ScanHistory Scans { get; }

    /// <summary>What belongs to the project rather than the laptop. Schema version 3.</summary>
    public ProjectSettings Settings { get; }

    /// <summary>Highest schema version this build understands.</summary>
    public static int SupportedSchemaVersion => SchemaMigrations.CurrentVersion;

    /// <summary>
    /// Opens a project file, creating it if it does not exist, and migrates it up to the current
    /// schema. Throws <see cref="SchemaVersionException"/> if the file came from a newer build.
    /// </summary>
    /// <param name="path">Path to the .netcproj file.</param>
    /// <param name="name">Display name used only when creating a new file. Defaults to the file name.</param>
    /// <param name="timeProvider">Clock, for tests.</param>
    public static ProjectStore Open(string path, string? name = null, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string full = System.IO.Path.GetFullPath(path);
        string? directory = System.IO.Path.GetDirectoryName(full);

        try
        {
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PersistenceException($"Cannot create the folder for '{full}': {ex.Message}", ex)
            {
                Remediation = "Pick a folder you can write to - a plant laptop often blocks writes outside "
                    + "your user profile.",
            };
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = full,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,

            // Pooling off so that Dispose really closes the handle. A pooled connection keeps the
            // file locked on Windows, and the very next thing a user does is rename, copy or
            // delete the project they just closed.
            Pooling = false,
        };

        string displayName = name ?? System.IO.Path.GetFileNameWithoutExtension(full);
        return Create(builder.ToString(), full, displayName, isInMemory: false, timeProvider);
    }

    /// <summary>
    /// A store that never touches disk.
    ///
    /// Not only for tests: the app has to be usable before anyone has saved a project, and an
    /// event log that only starts recording once you pick a filename is an event log that misses
    /// the first ten minutes - which is exactly when the interesting "why is nothing arriving"
    /// events happen. Start here, and offer to save later.
    /// </summary>
    public static ProjectStore CreateInMemory(string name = "Untitled", TimeProvider? timeProvider = null)
    {
        // A unique shared-cache name, so two in-memory stores in the same process cannot see each
        // other's tables.
        string source = $"netcontrol-{Guid.NewGuid():N}";

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = source,
            Mode = SqliteOpenMode.Memory,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
        };

        return Create(builder.ToString(), source, name, isInMemory: true, timeProvider);
    }

    private static ProjectStore Create(
        string connectionString, string filePath, string name, bool isInMemory, TimeProvider? timeProvider)
    {
        var connection = new SqliteConnection(connectionString);

        try
        {
            connection.Open();
            SchemaMigrations.Migrate(connection, filePath);
            EnsureProjectRow(connection, name, (timeProvider ?? TimeProvider.System).GetUtcNow());
        }
        catch (SchemaVersionException)
        {
            connection.Dispose();
            throw;
        }
        catch (SqliteException ex)
        {
            connection.Dispose();
            throw new PersistenceException($"Cannot open project '{filePath}': {ex.Message}", ex)
            {
                Remediation = "Check the file is not open in another program, is not read-only, and is not "
                    + "on a network share that has gone away.",
            };
        }
        catch
        {
            connection.Dispose();
            throw;
        }

        return new ProjectStore(connection, filePath, isInMemory, timeProvider ?? TimeProvider.System);
    }

    /// <summary>
    /// Copies this project - plan, assignments and the whole event log - to a file, and returns a
    /// store open on it. The caller owns both: dispose this one once it has swapped over.
    ///
    /// <para>This is what makes <see cref="CreateInMemory"/> honest. The app starts recording
    /// before anyone has picked a filename, so the first ten minutes of a session - which is
    /// exactly when the "why is nothing arriving" events happen - exist only in memory until
    /// somebody saves. A Save As that started an empty file would throw that away.</para>
    ///
    /// <para><b>Uses SQLite's online backup API, not <c>VACUUM INTO</c>.</b> The plan said vacuum
    /// and vacuum is the tidier statement, but through Microsoft.Data.Sqlite it silently produced
    /// an empty file from an in-memory source: no error, no rows, and a project that reopened
    /// looking like a brand new one. The backup API is the documented way to copy a live database
    /// - in-memory very much included - and it copies pages verbatim, so the schema, the
    /// append-only triggers and <c>PRAGMA user_version</c> all come across with the data. It runs
    /// under the store's lock, so the receive loop cannot write mid-copy.</para>
    ///
    /// <para>The counts are checked afterwards. That is not paranoia about the backup API; it is
    /// that we have now watched this operation lose a project's entire history without saying a
    /// word, and a commissioning record that is quietly empty is the worst failure this file has.
    /// Two COUNTs are a cheap price for never shipping that again.</para>
    ///
    /// <para><b>An existing file is refused, not replaced.</b> A project file is a commissioning
    /// record, and silently overwriting one because a file dialog defaulted to the wrong name is
    /// not a mistake anyone can undo. The caller confirms and deletes.</para>
    /// </summary>
    /// <param name="path">Destination. Must not exist.</param>
    /// <param name="name">
    /// Display name for the saved project. Defaults to the file name when this store has never had
    /// one of its own, and otherwise keeps the name it already has - saving "Line 3 panel" into
    /// backup.netcproj should not rename it to "backup".
    /// </param>
    public ProjectStore SaveAs(string path, string? name = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string full = System.IO.Path.GetFullPath(path);

        if (!IsInMemory && string.Equals(full, FilePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new PersistenceException($"This project is already '{FilePath}'.")
            {
                Remediation = "Pick a different file name, or just keep working - edits are written as you make them.",
            };
        }

        if (File.Exists(full))
        {
            throw new PersistenceException($"'{full}' already exists.")
            {
                Remediation = "Pick another name, or delete that file first if you really mean to replace it - "
                    + "a project file is somebody's commissioning record.",
            };
        }

        string? directory = System.IO.Path.GetDirectoryName(full);

        try
        {
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PersistenceException($"Cannot create the folder for '{full}': {ex.Message}", ex)
            {
                Remediation = "Pick a folder you can write to - a plant laptop often blocks writes outside "
                    + "your user profile.",
            };
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = full,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,

            // Same reason as Open: a pooled connection keeps the file locked on Windows, and the
            // next thing anyone does with a project they just saved is copy it onto a USB stick.
            Pooling = false,
        };

        // Read rather than InTransaction: the backup runs against a live database and needs no
        // transaction of its own. The lock is the part that matters.
        Read(connection =>
        {
            using var destination = new SqliteConnection(builder.ToString());
            destination.Open();
            connection.BackupDatabase(destination);
            return true;
        });

        ProjectStore? saved = null;

        try
        {
            saved = Open(full, name, Clock);
            VerifyCopy(saved);

            // The backup carried the old Project row across, so Open's conditional insert did
            // nothing and the old name came with it. Renaming afterwards is what gives an
            // "Untitled" in-memory project the identity of the file it has just become.
            string wanted = name
                ?? (IsInMemory ? System.IO.Path.GetFileNameWithoutExtension(full) : Project.Name);

            if (!string.IsNullOrWhiteSpace(wanted)
                && !string.Equals(wanted, saved.Project.Name, StringComparison.Ordinal))
            {
                saved.Rename(wanted);
            }

            return saved;
        }
        catch
        {
            // Never leave a half-written or empty project file behind for somebody to find later
            // and trust. Failing with nothing on disk is recoverable; failing with a plausible
            // looking but empty commissioning record is not. Dispose first - Windows will not
            // delete a file SQLite still has open.
            saved?.Dispose();
            TryDelete(full);
            throw;
        }
    }

    /// <summary>
    /// Confirms the copy actually holds what this store holds.
    ///
    /// <para>Rows, not bytes: the copy is reopened through the ordinary path, so this catches a
    /// truncated file, a copy that landed somewhere else, and the failure that prompted it - an
    /// operation that reported success and produced an empty database. Only the two tables that
    /// are the record get counted. The plan is what somebody typed and the event log is what
    /// happened; losing either without being told is the failure worth spending a query on.</para>
    /// </summary>
    private void VerifyCopy(ProjectStore saved)
    {
        int devices = Devices.Count;
        int events = Events.Count;

        if (saved.Devices.Count == devices && saved.Events.Count == events)
        {
            return;
        }

        throw new PersistenceException(
            $"'{saved.FilePath}' did not come out of the copy intact - expected {devices} planned device(s) and "
            + $"{events} event(s), found {saved.Devices.Count} and {saved.Events.Count}.")
        {
            Remediation = "Nothing was changed and the incomplete file has been removed. Try a different folder; "
                + "if it keeps happening, the project is still open here, so copy the plan out before closing it.",
        };
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The original failure is the one worth reporting. A leftover file is bad, but
            // replacing the explanation of what went wrong with "could not delete it either"
            // would leave the user with strictly less to go on.
        }
    }

    /// <summary>Changes the project's display name.</summary>
    public void Rename(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        InTransaction(connection =>
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE Project SET Name = $name WHERE Id = 1;";
            command.Parameters.AddWithValue("$name", name.Trim());
            return command.ExecuteNonQuery();
        });

        Project = LoadProject();
    }

    /// <summary>
    /// Runs work against the connection under the store's lock. Read-only by convention - no
    /// transaction is opened, because SQLite gives a single statement one anyway.
    /// </summary>
    internal T Read<T>(Func<SqliteConnection, T> work)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return Guarded(work);
        }
    }

    /// <summary>
    /// Runs work inside a transaction. Reentrant: a nested call joins the outer transaction
    /// rather than starting a second one, because SQLite has no nested transactions and the
    /// alternative is an exception the first time a repository calls another repository.
    /// </summary>
    internal T InTransaction<T>(Func<SqliteConnection, T> work)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_transactionDepth > 0)
            {
                _transactionDepth++;
                try
                {
                    return Guarded(work);
                }
                finally
                {
                    _transactionDepth--;
                }
            }

            using SqliteTransaction transaction = _connection.BeginTransaction();
            _transactionDepth++;
            try
            {
                T result = Guarded(work);
                transaction.Commit();
                return result;
            }
            finally
            {
                _transactionDepth--;
            }
        }
    }

    /// <summary>
    /// Turns a raw SQLite failure into something with a next action attached. Everything that
    /// reaches a user goes through here.
    /// </summary>
    private T Guarded<T>(Func<SqliteConnection, T> work)
    {
        try
        {
            return work(_connection);
        }
        catch (SqliteException ex)
        {
            throw new PersistenceException($"Project '{FilePath}' could not be updated: {ex.Message}", ex)
            {
                Remediation = ex.SqliteErrorCode switch
                {
                    8 or 13 => "The project file or its folder is read-only, or the disk is full.",
                    5 or 6 => "Another program is holding the project file. Close it and retry.",
                    _ => "If this keeps happening, copy the plan out and start a new project file.",
                },
            };
        }
    }

    private static void EnsureProjectRow(SqliteConnection connection, string name, DateTimeOffset now)
    {
        using SqliteTransaction transaction = connection.BeginTransaction();

        using (SqliteCommand insert = connection.CreateCommand())
        {
            // One project per file. The insert is conditional rather than unconditional so that
            // reopening an existing file leaves its name and creation date alone.
            insert.CommandText = """
                INSERT INTO Project (Id, Name, SchemaVersion, CreatedUtc)
                SELECT 1, $name, $version, $created
                WHERE NOT EXISTS (SELECT 1 FROM Project WHERE Id = 1);
                """;
            insert.Parameters.AddWithValue("$name", name);
            insert.Parameters.AddWithValue("$version", SchemaMigrations.CurrentVersion);
            insert.Parameters.AddWithValue("$created", SqlTime.ToSql(now));
            insert.ExecuteNonQuery();
        }

        using (SqliteCommand update = connection.CreateCommand())
        {
            // Mirror of PRAGMA user_version, which is what the migrator actually keys off. This
            // column exists so the version is visible to anyone opening the file in a SQL browser
            // without knowing to look at a pragma.
            update.CommandText = "UPDATE Project SET SchemaVersion = $version WHERE Id = 1;";
            update.Parameters.AddWithValue("$version", SchemaMigrations.CurrentVersion);
            update.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private ProjectInfo LoadProject() => Read(connection =>
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Name, SchemaVersion, CreatedUtc FROM Project WHERE Id = 1;";

        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new PersistenceException($"'{FilePath}' has no project row - the file is not a NetControl project.")
            {
                Remediation = "Open a different file, or create a new project.",
            };
        }

        return new ProjectInfo(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetInt32(2),
            SqlTime.FromSql(reader.GetString(3)));
    });

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _connection.Dispose();
        }
    }

    /// <summary>Handy in logs and window titles.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Project.Name} ({FilePath}, schema v{Project.SchemaVersion})");
}
