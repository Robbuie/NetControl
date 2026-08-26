using System.Net;
using Microsoft.Data.Sqlite;
using NetControl.Core;
using NetControl.Core.Persistence;
using Xunit;

namespace NetControl.Tests;

/// <summary>
/// Save As, which is what makes starting in memory an honest default.
///
/// The app records from its first second, before anyone has picked a filename, so the first ten
/// minutes of a session exist only in memory - and that is exactly the stretch where the "why is
/// nothing arriving" events happen. A Save As that started a fresh empty file would throw all of
/// it away, so the tests below are mostly about what has to survive the copy.
/// </summary>
public class ProjectStoreSaveAsTests
{
    private static readonly MacAddress Drive = MacAddress.Parse("00:1D:9C:C7:B0:70");

    [Fact]
    public void CarriesThePlanAndTheEventLogOutOfMemoryAndOntoDisk()
    {
        using var temp = new TempProject();

        using (ProjectStore memory = ProjectStore.CreateInMemory("Untitled"))
        {
            memory.Devices.Upsert(new DeviceRecord
            {
                Mac = Drive,
                PlannedIp = IPAddress.Parse("192.168.1.51"),
                PlannedMask = IPAddress.Parse("255.255.255.0"),
                HostName = "conveyor-3",
            });

            memory.Events.Append(EventSeverity.Info, EventCategory.Dhcp,"Watching. Nothing will be transmitted.");

            using ProjectStore saved = memory.SaveAs(temp.FilePath);
            Assert.False(saved.IsInMemory);
        }

        // Reopened from scratch, so nothing can be coming from the store that wrote it.
        using ProjectStore reopened = ProjectStore.Open(temp.FilePath);

        DeviceRecord? device = reopened.Devices.Find(Drive);
        Assert.NotNull(device);
        Assert.Equal("conveyor-3", device.HostName);
        Assert.Equal(IPAddress.Parse("192.168.1.51"), device.PlannedIp);

        Assert.Contains(
            reopened.Events.Recent(),
            e => e.Message.StartsWith("Watching", StringComparison.Ordinal));
    }

    /// <summary>
    /// The schema version travels with the file, not with the build that opened it. If VACUUM INTO
    /// dropped <c>PRAGMA user_version</c> the copy would look like a version 0 file and get
    /// migrated a second time on the next open.
    /// </summary>
    [Fact]
    public void KeepsTheSchemaVersionSoTheCopyIsNotMigratedAgain()
    {
        using var temp = new TempProject();
        using ProjectStore memory = ProjectStore.CreateInMemory();

        using ProjectStore saved = memory.SaveAs(temp.FilePath);

        Assert.Equal(ProjectStore.SupportedSchemaVersion, saved.Project.SchemaVersion);
    }

    /// <summary>
    /// The append-only triggers are part of the schema, so they come across too. Worth its own
    /// test: a commissioning record that could be rewritten after being saved would not be a
    /// record, and the ban is enforced by the database rather than by the API.
    /// </summary>
    [Fact]
    public void TheEventLogIsStillAppendOnlyAfterBeingSaved()
    {
        using var temp = new TempProject();

        using (ProjectStore memory = ProjectStore.CreateInMemory())
        {
            memory.Events.Append(EventSeverity.Info, EventCategory.Dhcp, "before the save");
            using ProjectStore saved = memory.SaveAs(temp.FilePath);
        }

        // Raw SQL, because nothing in this build has an API that would trip the trigger. The point
        // is that the ban survived the copy as a property of the file, not of the code.
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = temp.FilePath, Pooling = false }.ToString());
        connection.Open();

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Event;";

        var ex = Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
        Assert.Contains("append-only", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An existing file is refused rather than replaced. A file dialog defaulting to the wrong
    /// name must not be able to destroy somebody's commissioning record - the confirmation belongs
    /// to whoever put the dialog on screen, and Core's job is to make silence impossible.
    /// </summary>
    [Fact]
    public void RefusesToWriteOverAFileThatIsAlreadyThere()
    {
        using var temp = new TempProject();

        using (ProjectStore existing = temp.Open("Real work"))
        {
            existing.Devices.Upsert(new DeviceRecord { Mac = Drive, HostName = "do not lose me" });
        }

        using ProjectStore memory = ProjectStore.CreateInMemory();

        PersistenceException failure = Assert.Throws<PersistenceException>(() => memory.SaveAs(temp.FilePath));
        Assert.Contains("already exists", failure.Message, StringComparison.OrdinalIgnoreCase);

        using ProjectStore untouched = ProjectStore.Open(temp.FilePath);
        Assert.Equal("do not lose me", untouched.Devices.Find(Drive)?.HostName);
    }

    /// <summary>
    /// An in-memory project is "Untitled" and takes the identity of the file it becomes. A project
    /// that already had a name of its own keeps it - saving "Line 3 panel" into backup.netcproj
    /// should not rename it "backup".
    /// </summary>
    [Fact]
    public void AnUntitledProjectTakesTheNameOfItsNewFile()
    {
        using var temp = new TempProject("line-3-panel");
        using ProjectStore memory = ProjectStore.CreateInMemory();

        using ProjectStore saved = memory.SaveAs(temp.FilePath);

        Assert.Equal("line-3-panel", saved.Project.Name);
    }

    [Fact]
    public void ANamedProjectKeepsItsName()
    {
        using var source = new TempProject("original");
        using var destination = new TempProject("backup");

        using ProjectStore named = source.Open("Line 3 panel");
        using ProjectStore saved = named.SaveAs(destination.FilePath);

        Assert.Equal("Line 3 panel", saved.Project.Name);
    }

    /// <summary>Writes land in the new file, not the one that was open before.</summary>
    [Fact]
    public void LaterEditsGoToTheSavedFile()
    {
        using var temp = new TempProject();
        using ProjectStore memory = ProjectStore.CreateInMemory();

        using (ProjectStore saved = memory.SaveAs(temp.FilePath))
        {
            saved.Devices.Upsert(new DeviceRecord { Mac = Drive, HostName = "typed after saving" });
        }

        using ProjectStore reopened = ProjectStore.Open(temp.FilePath);
        Assert.Equal("typed after saving", reopened.Devices.Find(Drive)?.HostName);
    }
}
