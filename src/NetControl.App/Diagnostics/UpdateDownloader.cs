// UseWPF drops System.IO from the implicit usings; this file is entirely about files.
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using NetControl.Core.Diagnostics;

namespace NetControl.App.Diagnostics;

/// <summary>
/// Fetches a release asset and proves it is the one that was published.
///
/// <para><b>An asset with no published checksum is refused, and that is the important line in this
/// file.</b> Everything else here is plumbing. The next thing that happens to a verified download
/// is that it gets executed, on a laptop whose whole purpose is writing configuration into
/// somebody's plant equipment - so "we fetched something from the internet and ran it" needs to be
/// "we fetched the thing the release says it published, checked it byte for byte, and ran that".
/// The release workflow publishes a <c>.sha256</c> beside both artefacts; an asset without one is
/// either a hand-made release or not the release it claims to be, and neither is something to
/// install silently.</para>
///
/// <para>This is not a defence against somebody who controls the release page - they would publish
/// a matching checksum. It is a defence against the things that actually happen: a truncated
/// transfer, a proxy that returned a login page with a 200, a half-written file from a laptop that
/// went to sleep mid-download. Those are common, and every one of them produces an executable that
/// would otherwise be run.</para>
///
/// <para>Nothing here throws. Every failure is an <see cref="UpdateDownload.Refused"/>.</para>
/// </summary>
public static class UpdateDownloader
{
    /// <summary>
    /// Longer than the update check's five seconds, and for the opposite reason: nobody is waiting
    /// on the check, and somebody is definitely waiting on this. Tens of megabytes over a plant
    /// wireless link is minutes, not seconds.
    /// </summary>
    public static TimeSpan Timeout { get; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Downloads <paramref name="asset"/> into <paramref name="directory"/> and verifies it.
    /// </summary>
    /// <param name="progress">
    /// Fraction complete, 0 to 1, reported as bytes arrive. Never reported for the checksum file,
    /// which is eighty bytes.
    /// </param>
    public static async Task<UpdateDownload> DownloadAsync(
        UpdateAsset asset,
        string directory,
        IProgress<double>? progress = null,
        HttpMessageHandler? handler = null,
        ITraceLog? trace = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        ITraceLog log = trace ?? NullTraceLog.Instance;

        if (asset.ChecksumUrl is null)
        {
            // Before a single byte is fetched. There is no point downloading something that is
            // going to be refused, and saying so first makes the reason unambiguous in the log.
            log.Warn($"{asset.Name} publishes no SHA256 beside it; refusing to download it.");

            return UpdateDownload.Refused(
                $"The release publishes no checksum for {asset.Name}, so there is no way to tell "
                + "that what arrives is what was published. Download it by hand from the releases "
                + "page if you want it.");
        }

        string target = Path.Combine(directory, asset.Name);
        string partial = target + ".part";

        try
        {
            Directory.CreateDirectory(directory);

            using var client = handler is null
                ? new HttpClient()
                : new HttpClient(handler, disposeHandler: false);

            client.Timeout = Timeout;
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent", $"{BuildInfo.Product}/{BuildInfo.Version}");

            log.Info($"Downloading {asset.Url} to {target} ({asset.SizeText}).");

            // Fetched first, and deliberately: it is eighty bytes, so a release that cannot produce
            // one has cost nothing to find out about, and the alternative is discovering it after a
            // forty-megabyte download over a plant wireless link.
            string published = Parse(
                await client.GetStringAsync(asset.ChecksumUrl, cancellationToken).ConfigureAwait(false),
                asset.Name);

            if (published.Length == 0)
            {
                return UpdateDownload.Refused(
                    $"The checksum published for {asset.Name} could not be read.");
            }

            Delete(partial);

            long written = await CopyAsync(client, asset, partial, progress, cancellationToken)
                .ConfigureAwait(false);

            if (asset.Size > 0 && written != asset.Size)
            {
                // A transfer that ended early normally throws, but a proxy answering with a page of
                // its own does not - it answers 200 with a body, and the only thing wrong with it
                // is the length. The checksum below would catch this too; this catches it with a
                // sentence somebody can act on.
                Delete(partial);

                return UpdateDownload.Refused(
                    $"{asset.Name} should be {asset.Size:N0} bytes and {written:N0} arrived. "
                    + "Something between here and the release truncated or replaced it.");
            }

            string actual = await HashAsync(partial, cancellationToken).ConfigureAwait(false);

            if (!string.Equals(actual, published, StringComparison.OrdinalIgnoreCase))
            {
                // Deleted rather than kept. A file that failed verification is not evidence worth
                // preserving and it is an executable sitting in a folder.
                Delete(partial);
                log.Warn($"{asset.Name} hashed to {actual}; the release publishes {published}.");

                return UpdateDownload.Refused(
                    $"{asset.Name} does not match the checksum published with it. Nothing has been "
                    + "installed. Do not run a copy downloaded by hand either until you know why.");
            }

            // Only now does it get the name it will be run under. A .part on disk is unmistakably
            // an unfinished download; a NetControl-Setup-0.6.0.exe is something somebody will
            // double-click.
            Delete(target);
            File.Move(partial, target);

            log.Info($"{asset.Name} downloaded and verified: SHA256 {actual}.");

            return UpdateDownload.Verified(target, actual);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Delete(partial);
            log.Info($"The download of {asset.Name} was cancelled.");

            return UpdateDownload.Refused("Cancelled. Nothing has been installed.");
        }
        catch (OperationCanceledException ex)
        {
            // HttpClient reports its own timeout as a TaskCanceledException, which is an
            // OperationCanceledException - so without the filter above, a plant wireless link that
            // stalled for ten minutes would be reported to the user as "you cancelled it".
            Delete(partial);
            log.Warn($"The download of {asset.Name} timed out after {Timeout}.", ex);

            return UpdateDownload.Refused(
                $"{asset.Name} did not finish downloading within {Timeout.TotalMinutes:N0} minutes. "
                + "Nothing has been installed.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException
            or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            Delete(partial);
            log.Warn($"{asset.Name} could not be downloaded.", ex);

            return UpdateDownload.Refused($"{asset.Name} could not be downloaded: {ex.Message}");
        }
    }

    /// <summary>
    /// Streams the body to disk, reporting progress against the length the release stated rather
    /// than against Content-Length - the two agreeing is itself part of what is being checked.
    /// </summary>
    private static async Task<long> CopyAsync(
        HttpClient client,
        UpdateAsset asset,
        string path,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await client
            .GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        long total = asset.Size > 0 ? asset.Size : response.Content.Headers.ContentLength ?? 0;
        long written = 0;
        double lastReported = -1;

        using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        using var destination = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);

        byte[] buffer = new byte[64 * 1024];

        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

            if (read == 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);

            written += read;

            if (progress is null || total <= 0)
            {
                continue;
            }

            // Whole percent only. A progress bar told about every 64 KB of a forty-megabyte file
            // is six hundred dispatcher hops that change nothing anybody can see.
            double fraction = Math.Round((double)written / total, 2);

            if (fraction > lastReported)
            {
                lastReported = fraction;
                progress.Report(fraction);
            }
        }

        return written;
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);

        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);

        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// Reads the hash out of a <c>.sha256</c> file.
    ///
    /// <para>The shape <c>publish.ps1</c> writes is <c>&lt;hash&gt;  &lt;filename&gt;</c>, which is
    /// also what <c>sha256sum</c> writes, so a file produced by either is read. When the file names
    /// several files - which nothing here does today, but a hand-made release might - the line for
    /// <paramref name="expectedName"/> is the one taken, because picking the first line of a
    /// multi-file checksum would verify the wrong artefact and say nothing about it.</para>
    /// </summary>
    internal static string Parse(string content, string expectedName)
    {
        string? single = null;
        int lines = 0;

        foreach (string line in content.Split('\n'))
        {
            string[] parts = line.Trim().Split(
                (char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (parts.Length == 0 || !IsHex(parts[0]))
            {
                continue;
            }

            lines++;
            single ??= parts[0];

            // The '*' is sha256sum's marker for a binary read, and is not part of the name.
            if (parts.Length > 1
                && string.Equals(parts[^1].TrimStart('*'), expectedName, StringComparison.OrdinalIgnoreCase))
            {
                return parts[0];
            }
        }

        // A file naming nothing at all is the ordinary hand-written case and is taken at its word;
        // one naming several files, none of them the one being checked, is not.
        return lines == 1 ? single ?? string.Empty : string.Empty;
    }

    private static bool IsHex(string text)
    {
        if (text.Length != 64)
        {
            return false;
        }

        foreach (char c in text)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Best effort, always. This runs on the failure paths, where throwing would replace
    /// a reportable problem with an unreportable one.</summary>
    private static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
        {
            // Nothing to do about it, and nothing worth stopping for.
        }
    }
}
