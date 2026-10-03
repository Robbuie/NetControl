// UseWPF drops System.IO from the implicit usings; this file reads and writes a file.
// See CLAUDE.md.
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using NetControl.App.Composition;
using NetControl.Core.Diagnostics;

namespace NetControl.App.Appearance;

/// <summary>
/// Where the chosen theme, accent and density are kept, at
/// <c>%LOCALAPPDATA%\NetControl\appearance.json</c>.
///
/// <para><b>Its own file rather than a section of <c>settings.json</c>, on purpose.</b>
/// <see cref="Diagnostics.AppSettings"/> is site configuration: hand-written, absent on most
/// machines, and never written by the application - a tool that rewrites the file an
/// administrator deployed will one day drop the setting they came to add. This one is the
/// opposite: the app owns it, writes it whenever a person presses OK, and it means nothing to
/// anybody else.</para>
///
/// <para>Reading it never throws. A file that is missing, malformed or unreadable produces the
/// defaults and a line in the diagnostic log, because a colour scheme that can stop the
/// application starting is a much worse thing than no colour scheme.</para>
/// </summary>
public static class AppearanceStore
{
    /// <summary>The file. Created on demand; absent until somebody picks something.</summary>
    public static string FilePath { get; } = Path.Combine(AppPaths.Data, "appearance.json");

    /// <summary>
    /// The saved choice, normalised against this build's catalogs. Junk in the file - a theme
    /// from a later version, a hand-typed name, a key that was removed - comes back as the
    /// application default rather than as unreadable chrome.
    /// </summary>
    public static AppearanceChoice Load(ITraceLog? trace = null)
    {
        ITraceLog log = trace ?? NullTraceLog.Instance;

        try
        {
            if (!File.Exists(FilePath))
            {
                log.Info($"No appearance file at {FilePath}; using {Theme.Defaults.Describe()}.");
                return Theme.Defaults;
            }

            AppearanceChoice? read = JsonSerializer.Deserialize<AppearanceChoice>(
                File.ReadAllText(FilePath),
                ReadOptions);

            // A file written before 0.9.0 has no "follow": somebody picked that theme on purpose,
            // so it stays put rather than starting to follow Windows by surprise. Only a first run
            // with no file at all gets the new default, which follows.
            if (read is not null && read.Follow is null)
            {
                read = read with { Follow = Theme.FollowOff };
            }

            AppearanceChoice choice = Theme.Normalise(read);
            log.Info($"Appearance read from {FilePath}: {choice.Describe()}.");
            return choice;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or NotSupportedException or ArgumentException)
        {
            log.Warn($"{FilePath} could not be read; using {Theme.Defaults.Describe()}.", ex);
            return Theme.Defaults;
        }
    }

    /// <summary>
    /// Writes the choice. Failing to write it is worth a log line and nothing more - the theme is
    /// already on screen, and a dialog saying the colour scheme could not be saved is a dialog
    /// about nothing.
    /// </summary>
    public static void Save(AppearanceChoice choice, ITraceLog? trace = null)
    {
        ITraceLog log = trace ?? NullTraceLog.Instance;

        try
        {
            Directory.CreateDirectory(AppPaths.Data);
            File.WriteAllText(
                FilePath,
                JsonSerializer.Serialize(Theme.Normalise(choice), WriteOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
        {
            log.Warn($"{FilePath} could not be written; this choice will not survive a restart.", ex);
        }
    }

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
}
