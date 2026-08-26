using Microsoft.Data.Sqlite;
using NetControl.Core.Persistence;
using Xunit;

namespace NetControl.Tests;

/// <summary>
/// Opening, creating and migrating a project file.
///
/// The migration tests are the ones that matter. Everything else here would be caught the first
/// time somebody ran the app; a broken migration is only discovered six months later, on a
/// customer site, holding the only copy of a commissioning record.
/// </summary>
public class ProjectStoreTests
{
    [Fact]
    public void CreatesAFileAndStampsItWithTheCurrentSchemaVersion()
    {
        using var temp = new TempProject();

        using (ProjectStore store = temp.Open("Line 4 retrofit"))
        {
            Assert.Equal("Line 4 retrofit", store.Project.Name);
            Assert.Equal(ProjectStore.SupportedSchemaVersion, store.Project.SchemaVersion);
            Assert.False(store.IsInMemory);
        }

        Assert.True(File.Exists(temp.FilePath));
    }

    [Fact]
    public void NamesANewProjectAfterItsFileWhenNobodySaysOtherwise()
    {
        using var temp = new TempProject("panel-a7");
        using ProjectStore store = temp.Open();

        Assert.Equal("panel-a7", store.Project.Name);
    }

    /// <summary>
    /// Reopening must not reset the project. The creation date in particular is part of the
    /// commissioning record - "when was this panel first addressed" is a real question.
    /// </summary>
    [Fact]
    public void ReopeningKeepsTheNameAndTheCreationDate()
    {
        using var temp = new TempProject();
        var clock = new TestTimeProvider();

        DateTimeOffset created;
        using (ProjectStore first = temp.Open("Original", clock))
        {
            created = first.Project.CreatedUtc;
        }

        clock.Advance(TimeSpan.FromDays(180));

        using ProjectStore second = temp.Open("Ignored because the file already exists", clock);

        Assert.Equal("Original", second.Project.Name);
        Assert.Equal(created, second.Project.CreatedUtc);
    }

    [Fact]
    public void SurvivesBeingClosedAndReopened()
    {
        using var temp = new TempProject();

        using (ProjectStore store = temp.Open())
        {
            store.Events.Append(EventSeverity.Info, EventCategory.App, "Project created.");
        }

        using ProjectStore reopened = temp.Open();

        Assert.Equal(1, reopened.Events.Count);
        Assert.Equal("Project created.", reopened.Events.Recent()[0].Message);
    }

    [Fact]
    public void RenamesWithoutTouchingAnythingElse()
    {
        using var temp = new TempProject();
        using ProjectStore store = temp.Open("Before");

        store.Rename("After");

        Assert.Equal("After", store.Project.Name);
        Assert.Equal(ProjectStore.SupportedSchemaVersion, store.Project.SchemaVersion);
    }

    /// <summary>
    /// Migration has to be idempotent, because every open runs it. A second open that tried to
    /// re-apply step 1 would fail on "table already exists" - which is exactly the bug this
    /// catches, and exactly the bug that would only show up on the second run of the day.
    /// </summary>
    [Fact]
    public void MigratesOnceAndThenLeavesTheFileAlone()
    {
        using var temp = new TempProject();

        for (int i = 0; i < 3; i++)
        {
            using ProjectStore store = temp.Open();
            Assert.Equal(ProjectStore.SupportedSchemaVersion, store.Project.SchemaVersion);
        }

        Assert.Equal(ProjectStore.SupportedSchemaVersion, UserVersionOf(temp.FilePath));
    }

    /// <summary>
    /// A file from a build that knows more than this one is not opened at all. Downgrading a
    /// schema means guessing at columns this build has never heard of, and quietly dropping data
    /// out of somebody's commissioning record on a hunch is not a recoverable mistake.
    /// </summary>
    [Fact]
    public void RefusesAFileWrittenByANewerBuild()
    {
        using var temp = new TempProject();

        // Created and migrated normally, then stamped as if a future build had written it.
        using (temp.Open())
        {
        }

        SetUserVersion(temp.FilePath, ProjectStore.SupportedSchemaVersion + 5);

        var ex = Assert.Throws<SchemaVersionException>(() => temp.Open());

        Assert.Equal(ProjectStore.SupportedSchemaVersion + 5, ex.FileVersion);
        Assert.Equal(ProjectStore.SupportedSchemaVersion, ex.SupportedVersion);
        Assert.NotNull(ex.Remediation);
    }

    /// <summary>
    /// A refused open must not leave the file locked. The user's next action is to go and find
    /// the newer build, and a held handle would make copying or moving the file fail for reasons
    /// nobody could explain.
    /// </summary>
    [Fact]
    public void ReleasesTheFileWhenItRefusesToOpenIt()
    {
        using var temp = new TempProject();

        using (temp.Open())
        {
        }

        SetUserVersion(temp.FilePath, ProjectStore.SupportedSchemaVersion + 1);
        Assert.Throws<SchemaVersionException>(() => temp.Open());

        // If the connection were still open, this would throw on Windows.
        File.Delete(temp.FilePath);
        Assert.False(File.Exists(temp.FilePath));
    }

    /// <summary>
    /// The app has to be usable before anyone has saved a project. An event log that only starts
    /// recording once you pick a filename misses the first ten minutes - which is when the
    /// interesting "why is nothing arriving" events happen.
    /// </summary>
    [Fact]
    public void WorksEntirelyInMemoryWithNoFile()
    {
        using ProjectStore store = ProjectStore.CreateInMemory("Untitled");

        store.Events.Append(EventSeverity.Warn, EventCategory.Dhcp, "UDP/67 is held by vmnetdhcp.exe.");

        Assert.True(store.IsInMemory);
        Assert.Equal(1, store.Events.Count);
    }

    [Fact]
    public void TwoInMemoryStoresCannotSeeEachOther()
    {
        using ProjectStore first = ProjectStore.CreateInMemory();
        using ProjectStore second = ProjectStore.CreateInMemory();

        first.Events.Append(EventSeverity.Info, EventCategory.App, "Only in the first.");

        Assert.Equal(1, first.Events.Count);
        Assert.Equal(0, second.Events.Count);
    }

    [Fact]
    public void RefusesToBeUsedAfterDisposal()
    {
        ProjectStore store = ProjectStore.CreateInMemory();
        store.Dispose();

        Assert.Throws<ObjectDisposedException>(
            () => store.Events.Append(EventSeverity.Info, EventCategory.App, "Too late."));
    }

    private static int UserVersionOf(string path)
    {
        using SqliteConnection connection = Connect(path);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void SetUserVersion(string path, int version)
    {
        using SqliteConnection connection = Connect(path);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA user_version = {version};";
        command.ExecuteNonQuery();
    }

    private static SqliteConnection Connect(string path)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }
}
