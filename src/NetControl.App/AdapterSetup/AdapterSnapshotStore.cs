// UseWPF drops System.IO from the implicit usings; this file reads and writes a file. See CLAUDE.md.
using System.IO;
using System.Text.Json;
using NetControl.App.Composition;
using NetControl.Core.AdapterSetup;
using NetControl.Core.Diagnostics;

namespace NetControl.App.AdapterSetup;

/// <summary>
/// Where each adapter's settings from before NetControl first changed it are kept, so Restore puts
/// back what the laptop had - the office network, typically - even after the application has been
/// closed and reopened. Per laptop, not per project: it is this machine's settings.
/// </summary>
public interface IAdapterSnapshotStore
{
    /// <summary>The settings saved for this adapter before it was first changed, or null.</summary>
    AdapterSnapshot? Get(string adapterName);

    void Set(AdapterSnapshot snapshot);

    void Clear(string adapterName);
}

/// <summary><c>%LOCALAPPDATA%\NetControl\adapter-snapshots.json</c>, read and written whole.</summary>
public sealed class AdapterSnapshotFile(string? path = null, ITraceLog? trace = null) : IAdapterSnapshotStore
{
    private readonly string _path = path ?? Path.Combine(AppPaths.Data, "adapter-snapshots.json");
    private readonly ITraceLog _trace = trace ?? NullTraceLog.Instance;

    public AdapterSnapshot? Get(string adapterName) =>
        ReadAll().TryGetValue(adapterName, out string? json) ? AdapterSnapshot.FromJson(json) : null;

    public void Set(AdapterSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        Dictionary<string, string> all = ReadAll();
        all[snapshot.Name] = snapshot.ToJson();
        WriteAll(all);
    }

    public void Clear(string adapterName)
    {
        Dictionary<string, string> all = ReadAll();
        if (all.Remove(adapterName))
        {
            WriteAll(all);
        }
    }

    private Dictionary<string, string> ReadAll()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _trace.Warn($"{_path} could not be read; no saved adapter settings.", ex);
            return [];
        }
    }

    private void WriteAll(Dictionary<string, string> all)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _trace.Warn($"{_path} could not be written; Restore will not survive closing the application.", ex);
        }
    }
}

/// <summary>Kept in memory only - for tests, and for a laptop whose profile folder cannot be written.</summary>
public sealed class InMemoryAdapterSnapshotStore : IAdapterSnapshotStore
{
    private readonly Dictionary<string, AdapterSnapshot> _all = [];

    public AdapterSnapshot? Get(string adapterName) => _all.GetValueOrDefault(adapterName);

    public void Set(AdapterSnapshot snapshot) => _all[snapshot.Name] = snapshot;

    public void Clear(string adapterName) => _all.Remove(adapterName);
}
