using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using NetControl.Core.Diagnostics;

namespace NetControl.App.Diagnostics;

/// <summary>
/// Asks whether a newer build has been published, and says so. It does not download anything and it
/// does not run anything.
///
/// <para><b>Where it asks.</b> By default, the repository's latest GitHub release -
/// <see cref="DefaultSource"/>. A site that mirrors builds onto an intranet share points
/// <see cref="AppSettings.UpdateManifestUrl"/> at its own manifest instead, and a site that wants
/// this tool to make no outbound request at all sets <c>checkForUpdates</c> to false, which is the
/// one state in which nothing is contacted.</para>
///
/// <para><b>Two shapes are understood, and which one is in front of it is decided by the JSON
/// rather than by the URL.</b> A document carrying <c>tag_name</c> is a GitHub release; anything
/// else is the trivial manifest <c>publish.ps1</c> writes:</para>
/// <code>
/// { "version": "0.6.0", "url": "https://...", "notes": "Adds the CLI" }
/// </code>
///
/// <para>Nothing here throws. A failed check is a result, not an exception: the tool's job is
/// commissioning panels, and it must not be interrupted by not knowing what the latest version is.
/// </para>
/// </summary>
public static class UpdateCheck
{
    /// <summary>Short on purpose. Nobody should wait on this, and on a plant network it will fail.</summary>
    public static TimeSpan Timeout { get; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The latest published release of this repository. Asked for by the API rather than by
    /// scraping the releases page, because the API answers with the tag and nothing else - and
    /// <c>/releases/latest</c> already excludes drafts and pre-releases, so a tag pushed to see
    /// whether the workflow runs does not tell a plant laptop it is out of date.
    /// </summary>
    public static string DefaultSource { get; } =
        $"https://api.github.com/repos/{BuildInfo.RepositoryOwner}/{BuildInfo.RepositoryName}/releases/latest";

    public static async Task<UpdateResult> RunAsync(
        AppSettings settings,
        string currentVersion,
        HttpMessageHandler? handler = null,
        ITraceLog? trace = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentVersion);

        ITraceLog log = trace ?? NullTraceLog.Instance;

        if (!settings.CheckForUpdates)
        {
            // The only path that contacts nothing. Asserted by a test that counts the handler's
            // calls rather than by the result alone, because "we did not ask" is the claim.
            log.Info("Update check is turned off in the settings file; contacting nothing.");
            return UpdateResult.TurnedOff;
        }

        string url = string.IsNullOrWhiteSpace(settings.UpdateManifestUrl)
            ? DefaultSource
            : settings.UpdateManifestUrl;

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? manifest))
        {
            return new UpdateResult(UpdateAvailability.Failed, Problem: $"'{url}' is not a URL.");
        }

        try
        {
            log.Info($"Checking {manifest} for a newer build than {currentVersion}.");

            using var client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
            client.Timeout = Timeout;

            // GitHub answers 403 to a request with no User-Agent, and the failure would read as a
            // rate limit rather than as a missing header. TryAddWithoutValidation because the
            // version string carries a '+' and a commit hash, and a header that will not parse must
            // not be the thing that throws.
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent", $"{BuildInfo.Product}/{currentVersion}");
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "Accept", "application/vnd.github+json");
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "X-GitHub-Api-Version", "2022-11-28");

            string json = await client.GetStringAsync(manifest, cancellationToken).ConfigureAwait(false);

            using JsonDocument document = JsonDocument.Parse(json);

            bool isRelease = Has(document, "tag_name");

            // The trap here is GitHub's own "url", which is the API address of the release object -
            // not a page anybody can open. The human one is html_url, and reading the wrong one puts
            // an api.github.com link in front of somebody who wants to download an installer.
            string? latest = isRelease
                ? StripTagPrefix(Read(document, "tag_name"))
                : Read(document, "version");

            string? where = isRelease ? Read(document, "html_url") : Read(document, "url");

            // Deliberately not GitHub's "body": that is the whole release note, several paragraphs
            // of it, and this string goes in a status bar.
            string? notes = isRelease ? null : Read(document, "notes");

            if (latest is null)
            {
                return new UpdateResult(
                    UpdateAvailability.Failed,
                    Problem: $"{manifest} does not name a version.");
            }

            bool newer = IsNewer(latest, currentVersion);
            log.Info($"{manifest} publishes {latest}; running {currentVersion}. Newer: {newer}.");

            // Only a release carries files. A mirrored manifest names a version and a page, so a
            // site using one gets the check and not the install - which is the honest answer rather
            // than a guess at where its mirror keeps the exe.
            (UpdateAsset? installer, UpdateAsset? portable) = isRelease
                ? Assets(document)
                : (null, null);

            return new UpdateResult(
                newer ? UpdateAvailability.UpdateAvailable : UpdateAvailability.Current,
                latest,
                where,
                notes,
                Installer: installer,
                PortableExe: portable);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
            or InvalidOperationException or UriFormatException)
        {
            // Includes the timeout, which on a segment with no route out is the expected answer.
            log.Warn($"Update check against {manifest} did not complete.", ex);
            return new UpdateResult(UpdateAvailability.Failed, Problem: ex.Message);
        }
    }

    /// <summary>
    /// Compares the leading numbers of two versions.
    ///
    /// <para>Only the numeric part, and only as far as both go: <c>0.6.0+a1b2c3d</c> against
    /// <c>0.5.0</c> is newer, and <c>0.5.0+a1b2c3d</c> against <c>0.5.0</c> is not - a commit
    /// suffix is which build, not which version. A leading <c>v</c> is tolerated because release
    /// tags carry one. Anything that will not parse is treated as not newer, because the failure
    /// mode of guessing is telling somebody to go and find an update that does not exist.</para>
    /// </summary>
    public static bool IsNewer(string published, string running)
    {
        int[] left = Numbers(published);
        int[] right = Numbers(running);

        if (left.Length == 0 || right.Length == 0)
        {
            return false;
        }

        for (int i = 0; i < Math.Max(left.Length, right.Length); i++)
        {
            int a = i < left.Length ? left[i] : 0;
            int b = i < right.Length ? right[i] : 0;

            if (a != b)
            {
                return a > b;
            }
        }

        return false;
    }

    private static int[] Numbers(string version)
    {
        string text = StripTagPrefix(version) ?? string.Empty;

        // Everything up to the first thing that is not a digit or a dot: 0.6.0-rc1+abc -> 0.6.0
        int end = 0;
        while (end < text.Length && (char.IsAsciiDigit(text[end]) || text[end] == '.'))
        {
            end++;
        }

        string[] parts = text[..end].Split('.', StringSplitOptions.RemoveEmptyEntries);
        var numbers = new List<int>(parts.Length);

        foreach (string part in parts)
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out int value))
            {
                return [];
            }

            numbers.Add(value);
        }

        return [.. numbers];
    }

    /// <summary>"v0.6.0" -> "0.6.0". Only a leading v, and only when a digit follows it.</summary>
    private static string? StripTagPrefix(string? tag)
    {
        if (tag is null)
        {
            return null;
        }

        string trimmed = tag.Trim();

        return trimmed.Length > 1 && (trimmed[0] is 'v' or 'V') && char.IsAsciiDigit(trimmed[1])
            ? trimmed[1..]
            : trimmed;
    }

    /// <summary>
    /// Picks the two artefacts out of a release's assets, each paired with the <c>.sha256</c>
    /// published beside it.
    ///
    /// <para>Matched by name rather than by position, because the order assets come back in is
    /// whatever order they were uploaded. The installer is the <c>.exe</c> whose name says
    /// <c>setup</c>; the portable build is the one called exactly <c>NetControl.exe</c>. Anything
    /// else attached to a release - <c>version.json</c>, the checksums themselves, whatever a future
    /// release adds - is ignored rather than guessed at, and an asset this tool cannot recognise
    /// leaves the corresponding half null, which the dialog reports as "the releases page is all
    /// there is". Guessing would download something and then run it.</para>
    /// </summary>
    private static (UpdateAsset? Installer, UpdateAsset? Portable) Assets(JsonDocument document)
    {
        if (!document.RootElement.TryGetProperty("assets", out JsonElement assets)
            || assets.ValueKind != JsonValueKind.Array)
        {
            return (null, null);
        }

        var byName = new Dictionary<string, (Uri Url, long Size)>(StringComparer.OrdinalIgnoreCase);

        foreach (JsonElement asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object
                || !asset.TryGetProperty("name", out JsonElement name)
                || name.ValueKind != JsonValueKind.String
                || !asset.TryGetProperty("browser_download_url", out JsonElement url)
                || url.ValueKind != JsonValueKind.String
                || !Uri.TryCreate(url.GetString(), UriKind.Absolute, out Uri? address))
            {
                continue;
            }

            long size = asset.TryGetProperty("size", out JsonElement bytes)
                && bytes.ValueKind == JsonValueKind.Number
                && bytes.TryGetInt64(out long value)
                    ? value
                    : 0;

            byName[name.GetString()!] = (address, size);
        }

        return (Find(byName, IsInstaller), Find(byName, IsPortable));
    }

    private static UpdateAsset? Find(
        Dictionary<string, (Uri Url, long Size)> assets, Func<string, bool> matches)
    {
        foreach (KeyValuePair<string, (Uri Url, long Size)> asset in assets)
        {
            if (!matches(asset.Key))
            {
                continue;
            }

            Uri? checksum = assets.TryGetValue(asset.Key + ".sha256", out (Uri Url, long Size) sha)
                ? sha.Url
                : null;

            return new UpdateAsset(asset.Key, asset.Value.Url, asset.Value.Size, checksum);
        }

        return null;
    }

    private static bool IsInstaller(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        && name.Contains("setup", StringComparison.OrdinalIgnoreCase);

    private static bool IsPortable(string name) =>
        string.Equals(name, "NetControl.exe", StringComparison.OrdinalIgnoreCase);

    private static bool Has(JsonDocument document, string name) =>
        document.RootElement.ValueKind == JsonValueKind.Object
        && document.RootElement.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String;

    private static string? Read(JsonDocument document, string name) =>
        document.RootElement.ValueKind == JsonValueKind.Object
        && document.RootElement.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
