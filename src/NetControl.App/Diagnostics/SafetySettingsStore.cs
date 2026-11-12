// UseWPF drops System.IO from the implicit usings; this file reads and writes a file. See CLAUDE.md.
using System.IO;
using System.Text.Json;
using NetControl.App.Composition;
using NetControl.Core.Diagnostics;

namespace NetControl.App.Diagnostics;

/// <summary>
/// Reads and writes <c>%LOCALAPPDATA%\NetControl\safety.json</c>. Its own file, like
/// <c>appearance.json</c> and <c>tftp.json</c>: <c>settings.json</c> is site configuration the app
/// must never rewrite. Never throws - a file that cannot be read gives the defaults, which transmit
/// normally, and that is said in the diagnostic log.
/// </summary>
public static class SafetySettingsStore
{
    public static string FilePath { get; } = Path.Combine(AppPaths.Data, "safety.json");

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static SafetySettings Load(ITraceLog? trace = null) => Load(FilePath, trace);

    public static SafetySettings Load(string path, ITraceLog? trace = null)
    {
        ITraceLog log = trace ?? NullTraceLog.Instance;

        try
        {
            if (!File.Exists(path))
            {
                return new SafetySettings();
            }

            return JsonSerializer.Deserialize<SafetySettings>(File.ReadAllText(path), ReadOptions) ?? new SafetySettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or NotSupportedException or ArgumentException)
        {
            log.Warn($"{path} could not be read; listen-only is off and the send cap is the default.", ex);
            return new SafetySettings();
        }
    }

    public static void Save(SafetySettings settings, ITraceLog? trace = null) => Save(FilePath, settings, trace);

    public static void Save(string path, SafetySettings settings, ITraceLog? trace = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ITraceLog log = trace ?? NullTraceLog.Instance;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(settings, WriteOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
        {
            log.Warn($"{path} could not be written; this choice will not survive a restart.", ex);
        }
    }
}
