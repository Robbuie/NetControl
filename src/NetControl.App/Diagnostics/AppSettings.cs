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
/// That is the design, not an oversight: the tool is installed or copied onto a plant laptop and
/// run, and anything that only works once somebody has hand-written a JSON file is something that
/// does not work. The file exists so a site can change how the tool behaves - currently only the
/// update check - without a different build.</para>
///
/// <para><b>The installer must never write this file.</b> It is site configuration, and an
/// installer that rewrote it would undo a decision somebody made deliberately, most likely the
/// decision to stop this tool touching the network. The uninstaller leaves it behind for the same
/// reason.</para>
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
    /// Whether to ask, at startup, if a newer build has been published. True by default.
    ///
    /// <para><b>This is the one switch that stops the tool making any outbound request at all</b>,
    /// and it exists because of where this runs. Plenty of plant segments have no route out, and on
    /// a few an unexplained outbound connection is a reportable incident. Setting it to false is a
    /// site saying so, and after that nothing is contacted - asserted by a test that counts calls on
    /// the HTTP handler, not just by what the tool reports.</para>
    ///
    /// <para>Left on, the check itself is one GET of a small JSON document with a five-second
    /// timeout; nothing is downloaded unless somebody presses Install, and nothing is run that has
    /// not been checked against the checksum published with it. A check that fails at startup is
    /// silent, because on a segment with no route out that is the expected answer every launch; one
    /// somebody asked for through <b>Help &gt; Check for updates</b> always answers.</para>
    /// </summary>
    [JsonPropertyName("checkForUpdates")]
    public bool CheckForUpdates { get; init; } = true;

    /// <summary>
    /// Where to look, when the default - this repository's latest GitHub release - is not the right
    /// answer. Null on almost every machine.
    ///
    /// <para>It is here for the site that mirrors builds onto its own intranet or a file share,
    /// which is the ordinary shape of things in a plant that does not let laptops reach github.com.
    /// Any URL returning the manifest <c>publish.ps1</c> writes will do, including a <c>file://</c>
    /// path. Setting this does not turn the check on or off; <see cref="CheckForUpdates"/> does
    /// that.</para>
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
