// UseWPF drops System.IO from the implicit usings; this file reads and writes a file. See CLAUDE.md.
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using NetControl.App.Composition;
using NetControl.Core.Diagnostics;

namespace NetControl.App.Serving;

/// <summary>
/// Reads and writes <c>%LOCALAPPDATA%\NetControl\tftp.json</c>, the same way
/// <c>AppearanceStore</c> handles its file: a missing or unreadable file is the defaults, and a
/// failed write is a line in the diagnostic log and nothing more.
/// </summary>
public static class TftpSettingsStore
{
    public static string FilePath { get; } = Path.Combine(AppPaths.Data, "tftp.json");

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static TftpSettings Load(ITraceLog? trace = null)
    {
        ITraceLog log = trace ?? NullTraceLog.Instance;

        try
        {
            if (!File.Exists(FilePath))
            {
                return new TftpSettings();
            }

            return JsonSerializer.Deserialize<TftpSettings>(File.ReadAllText(FilePath), ReadOptions)
                ?? new TftpSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or NotSupportedException or ArgumentException)
        {
            log.Warn($"{FilePath} could not be read; the TFTP tab starts empty.", ex);
            return new TftpSettings();
        }
    }

    public static void Save(TftpSettings settings, ITraceLog? trace = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ITraceLog log = trace ?? NullTraceLog.Instance;

        try
        {
            Directory.CreateDirectory(AppPaths.Data);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, WriteOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
        {
            log.Warn($"{FilePath} could not be written; the TFTP tab will not remember this.", ex);
        }
    }
}
