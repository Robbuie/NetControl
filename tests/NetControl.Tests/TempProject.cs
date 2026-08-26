using NetControl.Core.Persistence;

namespace NetControl.Tests;

/// <summary>
/// A real project file in a temporary folder, cleaned up afterwards.
///
/// Deliberately a file rather than <see cref="ProjectStore.CreateInMemory"/> for the tests that
/// care about persistence: the whole point of most of them is that something survives being
/// closed and reopened, and an in-memory store cannot demonstrate that.
/// </summary>
internal sealed class TempProject : IDisposable
{
    private readonly string _directory;

    public TempProject(string? name = null)
    {
        _directory = Path.Combine(Path.GetTempPath(), "netcontrol-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        FilePath = Path.Combine(_directory, $"{name ?? "project"}.netcproj");
    }

    /// <summary>Where the file lives. It does not exist until something opens it.</summary>
    public string FilePath { get; }

    public ProjectStore Open(string? name = null, TimeProvider? time = null) =>
        ProjectStore.Open(FilePath, name, time);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp folder is not worth failing a test over. Pooling is off on the
            // store's connection precisely so this should not happen, but a virus scanner can
            // still be holding the file open for a moment.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }
}
