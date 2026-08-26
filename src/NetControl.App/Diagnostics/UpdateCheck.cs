using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using NetControl.Core.Diagnostics;

namespace NetControl.App.Diagnostics;

/// <summary>
/// Asks a published manifest whether there is a newer build, and says so. It does not download
/// anything, it does not run anything, and it does nothing at all unless somebody has configured a
/// URL - see <see cref="AppSettings.UpdateManifestUrl"/>.
///
/// <para><b>Off by default is the whole design.</b> This runs on plant laptops, frequently on
/// segments with no route out and occasionally on networks where an unexplained outbound request is
/// a reportable incident. So the tool makes no connection anybody did not ask for, and when it does
/// make one it is a single GET of a small JSON file with a short timeout.</para>
///
/// <para>The manifest is deliberately trivial, so it can be a file on a share, a page on an
/// intranet, or a raw file in a repository:</para>
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

    public static async Task<UpdateResult> RunAsync(
        AppSettings settings,
        string currentVersion,
        HttpMessageHandler? handler = null,
        ITraceLog? trace = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentVersion);

        if (string.IsNullOrWhiteSpace(settings.UpdateManifestUrl))
        {
            return UpdateResult.NotConfigured;
        }

        string url = settings.UpdateManifestUrl;

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? manifest))
        {
            return new UpdateResult(UpdateAvailability.Failed, Problem: $"'{url}' is not a URL.");
        }

        try
        {
            trace?.Info($"Checking {manifest} for a newer build than {currentVersion}.");

            using var client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
            client.Timeout = Timeout;

            string json = await client.GetStringAsync(manifest, cancellationToken).ConfigureAwait(false);

            using JsonDocument document = JsonDocument.Parse(json);

            string? latest = Read(document, "version");

            if (latest is null)
            {
                return new UpdateResult(
                    UpdateAvailability.Failed,
                    Problem: $"{manifest} does not name a version.");
            }

            bool newer = IsNewer(latest, currentVersion);
            trace?.Info($"{manifest} publishes {latest}; running {currentVersion}. Newer: {newer}.");

            return new UpdateResult(
                newer ? UpdateAvailability.UpdateAvailable : UpdateAvailability.Current,
                latest,
                Read(document, "url"),
                Read(document, "notes"));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
            or InvalidOperationException or UriFormatException)
        {
            // Includes the timeout, which on a segment with no route out is the expected answer.
            trace?.Warn($"Update check against {manifest} did not complete.", ex);
            return new UpdateResult(UpdateAvailability.Failed, Problem: ex.Message);
        }
    }

    /// <summary>
    /// Compares the leading numbers of two versions.
    ///
    /// <para>Only the numeric part, and only as far as both go: <c>0.6.0+a1b2c3d</c> against
    /// <c>0.5.0</c> is newer, and <c>0.5.0+a1b2c3d</c> against <c>0.5.0</c> is not - a commit
    /// suffix is which build, not which version. Anything that will not parse is treated as not
    /// newer, because the failure mode of guessing is telling somebody to go and find an update
    /// that does not exist.</para>
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
        // Everything up to the first thing that is not a digit or a dot: 0.6.0-rc1+abc -> 0.6.0
        int end = 0;
        while (end < version.Length && (char.IsAsciiDigit(version[end]) || version[end] == '.'))
        {
            end++;
        }

        string[] parts = version[..end].Split('.', StringSplitOptions.RemoveEmptyEntries);
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

    private static string? Read(JsonDocument document, string name) =>
        document.RootElement.ValueKind == JsonValueKind.Object
        && document.RootElement.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
