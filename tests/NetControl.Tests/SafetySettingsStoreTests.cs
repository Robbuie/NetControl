using NetControl.App.Diagnostics;
using Xunit;

namespace NetControl.Tests;

public sealed class SafetySettingsStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "netcontrol-safety-" + Guid.NewGuid().ToString("N"), "safety.json");

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_path)!, recursive: true);
        }
        catch (IOException)
        {
            // Not a failing test.
        }
    }

    [Fact]
    public void RoundTripsAndAMissingFileGivesTheDefaults()
    {
        Assert.False(SafetySettingsStore.Load(_path).ListenOnly);

        SafetySettingsStore.Save(_path, new SafetySettings { ListenOnly = true, MaxPacketsPerSecond = 50, VerboseLog = true });
        SafetySettings loaded = SafetySettingsStore.Load(_path);

        Assert.True(loaded.ListenOnly);
        Assert.Equal(50, loaded.MaxPacketsPerSecond);
        Assert.True(loaded.VerboseLog);
    }

    [Fact]
    public void AFileThatIsNotJsonGivesTheDefaultsRatherThanThrowing()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, "{ not json");

        Assert.Equal(new SafetySettings(), SafetySettingsStore.Load(_path));
    }
}
