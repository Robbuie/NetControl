using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using NetControl.App.Diagnostics;
using Xunit;

namespace NetControl.Tests;

/// <summary>
/// Fetching a release asset, and refusing to hand back anything that is not provably the one that
/// was published.
///
/// <para>Everything here is about that refusal, because of what happens next: the file this type
/// returns gets executed, on a laptop whose job is writing configuration into somebody's plant
/// equipment. The interesting cases are not "does a download work" - they are the three ways a
/// download can appear to work and not have.</para>
/// </summary>
public class UpdateDownloaderTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "netcontrol-tests", Path.GetRandomFileName());

    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("this stands in for an installer");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_folder))
            {
                Directory.Delete(_folder, recursive: true);
            }
        }
        catch (IOException)
        {
            // A test that cannot tidy up is not a test that failed.
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>The ordinary path: it arrives, it hashes to what was published, it is kept.</summary>
    [Fact]
    public async Task KeepsAnAssetThatMatchesItsPublishedChecksum()
    {
        var handler = new StubHandler(Payload, Sha256(Payload));

        UpdateDownload result = await UpdateDownloader.DownloadAsync(Asset(), _folder, handler: handler);

        Assert.True(result.IsVerified);
        Assert.NotNull(result.Path);
        Assert.Equal(Sha256(Payload), result.Sha256, ignoreCase: true);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(result.Path!));

        // Under the name it was published as, not a temporary one: a .part on disk is unmistakably
        // an unfinished download, and this file is one somebody may end up running by hand.
        Assert.Equal("NetControl-Setup-0.6.0.exe", Path.GetFileName(result.Path!));
    }

    /// <summary>
    /// The case this whole type exists for. A body that is not what was published is refused, and
    /// <b>the file is deleted</b> - a failed verification leaves no executable lying in a folder for
    /// somebody to find later and assume is fine.
    ///
    /// <para>The tampered body is <b>exactly as long</b> as the real one, and is derived from it
    /// rather than written out, so it cannot drift into being a different length later. The length
    /// is checked before the hash, so a body of the wrong size is refused for being the wrong size -
    /// which is a different refusal with a different message, and arranging one here would leave
    /// the checksum comparison itself untested. Getting this wrong is what the first run of this
    /// test did.</para>
    /// </summary>
    [Fact]
    public async Task RefusesAndDeletesAnAssetThatDoesNotMatch()
    {
        byte[] tampered = (byte[])Payload.Clone();
        tampered[0] ^= 0xFF;

        var handler = new StubHandler(tampered, Sha256(Payload));

        UpdateDownload result = await UpdateDownloader.DownloadAsync(Asset(), _folder, handler: handler);

        Assert.False(result.IsVerified);
        Assert.Null(result.Path);
        Assert.Contains("does not match the checksum", result.Problem!, StringComparison.Ordinal);

        Assert.Empty(Directory.Exists(_folder) ? Directory.GetFiles(_folder) : Array.Empty<string>());
    }

    /// <summary>
    /// No checksum published, nothing downloaded - and asserted by the handler counting its calls,
    /// because the claim is that not a byte is fetched rather than that it is thrown away
    /// afterwards. A forty-megabyte download over a plant wireless link is not something to do
    /// before discovering there was never a way to check it.
    /// </summary>
    [Fact]
    public async Task RefusesAnAssetWithNoPublishedChecksumWithoutFetchingIt()
    {
        var handler = new StubHandler(Payload, Sha256(Payload));

        var unverifiable = new UpdateAsset(
            "NetControl-Setup-0.6.0.exe",
            new Uri("https://example.invalid/NetControl-Setup-0.6.0.exe"),
            Payload.Length,
            ChecksumUrl: null);

        UpdateDownload result = await UpdateDownloader.DownloadAsync(unverifiable, _folder, handler: handler);

        Assert.False(result.IsVerified);
        Assert.Equal(0, handler.Calls);
        Assert.Contains("no checksum", result.Problem!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A proxy answering 200 with a page of its own is the failure that does not look like one: no
    /// exception, no truncation error, just the wrong bytes. The length is checked before the hash
    /// so the message can say what actually happened rather than "the checksum did not match".
    /// </summary>
    [Fact]
    public async Task RefusesABodyThatIsNotTheLengthTheReleaseStated()
    {
        byte[] loginPage = Encoding.UTF8.GetBytes("<html>Sign in to continue</html>");
        var handler = new StubHandler(loginPage, Sha256(Payload));

        UpdateDownload result = await UpdateDownloader.DownloadAsync(Asset(), _folder, handler: handler);

        Assert.False(result.IsVerified);
        Assert.Contains("truncated or replaced", result.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportsAnAssetThatCouldNotBeFetched()
    {
        var handler = new StubHandler(Payload, Sha256(Payload), assetStatus: HttpStatusCode.NotFound);

        UpdateDownload result = await UpdateDownloader.DownloadAsync(Asset(), _folder, handler: handler);

        Assert.False(result.IsVerified);
        Assert.NotNull(result.Problem);
    }

    /// <summary>Progress reaches 1, and the caller is never told about the checksum file's 64 bytes.</summary>
    [Fact]
    public async Task ReportsProgressUpToComplete()
    {
        var seen = new List<double>();
        var handler = new StubHandler(Payload, Sha256(Payload));

        await UpdateDownloader.DownloadAsync(
            Asset(), _folder, new SynchronousProgress(seen), handler);

        Assert.NotEmpty(seen);
        Assert.Equal(1d, seen[^1]);
        Assert.All(seen, fraction => Assert.InRange(fraction, 0d, 1d));
    }

    /// <summary>
    /// The shapes a <c>.sha256</c> comes in. <c>publish.ps1</c> writes hash-two-spaces-name, which
    /// is also what <c>sha256sum</c> writes; a hand-made one often holds the hash alone.
    ///
    /// <para>The last case is the one worth having: a file listing several artefacts, none of them
    /// the one being checked, must not have its first line taken. That would verify a different
    /// file and report success.</para>
    /// </summary>
    [Theory]
    [InlineData("AABB  NetControl-Setup-0.6.0.exe", "AABB")]
    [InlineData("AABB *NetControl-Setup-0.6.0.exe", "AABB")]
    [InlineData("AABB  netcontrol-setup-0.6.0.EXE", "AABB")]
    [InlineData("AABB", "AABB")]
    [InlineData("AABB\n", "AABB")]
    [InlineData("CCDD  NetControl.exe\nAABB  NetControl-Setup-0.6.0.exe", "AABB")]
    [InlineData("CCDD  NetControl.exe\nEEFF  version.json", "")]
    [InlineData("not a hash at all", "")]
    [InlineData("", "")]
    public void ReadsTheHashOutOfAChecksumFile(string content, string expected)
    {
        // The theory data carries short stand-ins so the cases stay readable; the parser only
        // accepts 64 hex characters, so they are padded out here rather than in every row.
        string padded = content.Replace("AABB", Repeat('a'), StringComparison.Ordinal)
            .Replace("CCDD", Repeat('c'), StringComparison.Ordinal)
            .Replace("EEFF", Repeat('e'), StringComparison.Ordinal);

        string want = expected.Length == 0 ? string.Empty : Repeat(expected[0]);

        Assert.Equal(
            want,
            UpdateDownloader.Parse(padded, "NetControl-Setup-0.6.0.exe"),
            ignoreCase: true);
    }

    private static string Repeat(char c) => new(char.ToLowerInvariant(c), 64);

    private static UpdateAsset Asset() => new(
        "NetControl-Setup-0.6.0.exe",
        new Uri("https://example.invalid/NetControl-Setup-0.6.0.exe"),
        Payload.Length,
        new Uri("https://example.invalid/NetControl-Setup-0.6.0.exe.sha256"));

    private static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    /// <summary>Reports on the calling thread, so a test can read the list straight afterwards.</summary>
    private sealed class SynchronousProgress(List<double> seen) : IProgress<double>
    {
        public void Report(double value) => seen.Add(value);
    }

    /// <summary>
    /// Answers the asset request with <paramref name="body"/> and the <c>.sha256</c> request with
    /// <paramref name="publishedHash"/>, told apart by the URL, so one stub covers both halves of
    /// what the downloader does.
    /// </summary>
    private sealed class StubHandler(
        byte[] body, string publishedHash, HttpStatusCode assetStatus = HttpStatusCode.OK)
        : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;

            bool checksum = request.RequestUri!.AbsoluteUri.EndsWith(
                ".sha256", StringComparison.OrdinalIgnoreCase);

            if (checksum)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        $"{publishedHash}  NetControl-Setup-0.6.0.exe", Encoding.UTF8, "text/plain"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(assetStatus)
            {
                Content = new ByteArrayContent(body),
            });
        }
    }
}
