// UseWPF drops System.IO from the implicit usings; this file reads a file. See CLAUDE.md.
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using NetControl.App.Composition;
using NetControl.Core.Diagnostics;

namespace NetControl.App.Diagnostics;

/// <summary>
/// The optional settings file, at <see cref="AppPaths.SettingsFile"/>.
///
/// <para><b>Every setting here has a working default, and the file is absent on most machines.</b>
/// That is the design, not an oversight: the tool is copied onto a plant laptop and run, and
/// anything that only works once somebody has hand-written a JSON file is something that does not
/// work. The file exists so a site can turn one thing on - currently the update check - without a
/// different build.</para>
///
/// <para>Reading it never throws and never blocks startup. A file that is missing, malformed, or
/// unreadable produces the defaults and a line in the diagnostic log, because a settings file that
/// can stop the application starting is worse than no settings file.</para>
/// </summary>
public sealed record AppSettings
{
    /// <summary>What every machine gets when there is no file, or the file cannot be read.</summary>
    public static AppSettings Defaults { get; } = new();

    /// <summary>
    /// Where to look for a published version manifest - see <c>UpdateCheck</c>.
    ///
    /// <para><b>Null by default, and null means the tool never touches the network.</b> Most of
    /// these run on segments with no route out, several run on networks where an unexplained
    /// outbound request is an incident, and a tool that phones home by default is a tool plant IT
    /// is right to refuse. Set it to a URL - GitHub, an intranet page, a file:// path on a share -
    /// and the tool will say when a newer build exists. It never downloads or installs anything.
    /// </para>
    /// </summary>
    [JsonPropertyName("updateManifestUrl")]
    public string? UpdateManifestUrl { get; init; }

    /// <summary>
    /// Reads the file if it is there. Returns <see cref="Defaults"/> otherwise, and says which in
    /// the diagnostic log so "I set that and it did nothing" has an answer.
    /// </summary>
    public static AppSettings Load(string path, ITraceLog? trace = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // NullTraceLog exists so nothing below has to ask whether there is a log.
        ITraceLog log = trace ?? NullTraceLog.Instance;

        try
        {
            if (!File.Exists(path))
            {
                log.Info($"No settings file at {path}; using defaults.");
                return Defaults;
            }

            AppSettings? read = JsonSerializer.Deserialize<AppSettings>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true, AllowTrailingCommas = true });

            if (read is null)
            {
                log.Warn($"{path} parsed to nothing; using defaults.");
                return Defaults;
            }

            log.Info($"Settings read from {path}.");
            return read;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or NotSupportedException or ArgumentException)
        {
            log.Warn($"{path} could not be read; using defaults.", ex);
            return Defaults;
        }
    }
}
