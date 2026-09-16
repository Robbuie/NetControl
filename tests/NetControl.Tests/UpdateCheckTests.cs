using System.Net;
using System.Net.Http;
using System.Text;
using NetControl.App.Diagnostics;
using Xunit;

namespace NetControl.Tests;

/// <summary>
/// The update check: whether a newer build has been published, and nothing else.
///
/// <para>The rule worth testing hardest is the one about not reaching the network. These run on
/// plant laptops, often on segments with no route out and occasionally on networks where an
/// unexplained outbound request is a reportable incident - so a site must be able to stop the tool
/// contacting anything at all, and the test for that is that the handler is never called.</para>
/// </summary>
public class UpdateCheckTests
{
    private const string Running = "0.5.0";

    /// <summary>
    /// <c>"checkForUpdates": false</c> is the whole of the promise, so it is asserted by the
    /// handler counting its calls rather than by the result alone.
    /// </summary>
    [Fact]
    public async Task ContactsNothingWhenTheCheckIsTurnedOff()
    {
        var handler = new StubHandler("{}");

        UpdateResult result = await UpdateCheck.RunAsync(
            new AppSettings { CheckForUpdates = false }, Running, handler);

        Assert.Equal(UpdateAvailability.TurnedOff, result.Availability);
        Assert.Equal(0, handler.Calls);

        // And says nothing in the status bar: a site that turned this off does not need reminding.
        Assert.Null(result.StatusText);
    }

    /// <summary>
    /// With no settings file at all the tool asks the repository's own latest release. The URL is
    /// asserted because the two halves of it - the API host and <c>/releases/latest</c> - are what
    /// keep a draft or a pre-release tag from telling a plant laptop it is out of date.
    /// </summary>
    [Fact]
    public async Task AsksTheRepositoryWhenNothingIsConfigured()
    {
        var handler = new StubHandler("""{ "tag_name": "v0.5.0" }""");

        UpdateResult result = await UpdateCheck.RunAsync(AppSettings.Defaults, Running, handler);

        Assert.Equal(1, handler.Calls);
        Assert.Equal(UpdateCheck.DefaultSource, handler.LastUrl);
        Assert.Contains("api.github.com", handler.LastUrl!, StringComparison.Ordinal);
        Assert.EndsWith("/releases/latest", handler.LastUrl!, StringComparison.Ordinal);
        Assert.Equal(UpdateAvailability.Current, result.Availability);
    }

    /// <summary>
    /// GitHub answers 403 to a request carrying no User-Agent, and the body of that refusal reads
    /// like a rate limit - which would send somebody looking in entirely the wrong place.
    /// </summary>
    [Fact]
    public async Task IdentifiesItselfSoGitHubWillAnswer()
    {
        var handler = new StubHandler("""{ "tag_name": "v0.5.0" }""");

        await UpdateCheck.RunAsync(AppSettings.Defaults, Running, handler);

        Assert.NotNull(handler.LastUserAgent);
        Assert.Contains("NetControl", handler.LastUserAgent!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A GitHub release, read as one: the tag is the version, and the page offered to the user is
    /// <c>html_url</c>.
    ///
    /// <para><b>Not <c>url</c>.</b> A release object carries one, and it is the API address of the
    /// release rather than anything a person can open - so a check that read it would put
    /// api.github.com in front of somebody looking for an installer. The stub below deliberately
    /// includes it.</para>
    /// </summary>
    [Fact]
    public async Task ReadsAGitHubReleaseAndOffersThePageAPersonCanOpen()
    {
        var handler = new StubHandler(
            """
            {
              "tag_name": "v0.6.0",
              "name": "NetControl 0.6.0",
              "url": "https://api.github.com/repos/Robbuie/netcontrol/releases/123",
              "html_url": "https://github.com/Robbuie/netcontrol/releases/tag/v0.6.0",
              "body": "Adds the CLI.\nSeveral paragraphs of release notes."
            }
            """);

        UpdateResult result = await UpdateCheck.RunAsync(AppSettings.Defaults, Running, handler);

        Assert.Equal(UpdateAvailability.UpdateAvailable, result.Availability);

        // The leading v is a tag convention, not part of the version anybody reads.
        Assert.Equal("0.6.0", result.LatestVersion);

        Assert.Equal("https://github.com/Robbuie/netcontrol/releases/tag/v0.6.0", result.DownloadUrl);
        Assert.DoesNotContain("api.github.com", result.StatusText!, StringComparison.Ordinal);

        // The release body is not the notes: it is several paragraphs, and this goes in a status bar.
        Assert.Null(result.Notes);
    }

    /// <summary>
    /// The two artefacts are picked out of the release's assets by name, each paired with the
    /// <c>.sha256</c> published beside it - which is what makes the download verifiable and
    /// therefore what makes installing it automatically defensible at all.
    ///
    /// <para>The stub carries a plausible fifth asset the tool knows nothing about. It is ignored
    /// rather than guessed at: the alternative is downloading something unrecognised and then
    /// running it.</para>
    /// </summary>
    [Fact]
    public async Task PicksTheInstallerAndThePortableExeOutOfARelease()
    {
        var handler = new StubHandler(
            """
            {
              "tag_name": "v0.6.0",
              "html_url": "https://github.com/Robbuie/netcontrol/releases/tag/v0.6.0",
              "assets": [
                { "name": "version.json",
                  "browser_download_url": "https://example.invalid/version.json", "size": 90 },
                { "name": "NetControl.exe",
                  "browser_download_url": "https://example.invalid/portable.exe", "size": 71000000 },
                { "name": "NetControl.exe.sha256",
                  "browser_download_url": "https://example.invalid/portable.sha256", "size": 80 },
                { "name": "NetControl-Setup-0.6.0.exe",
                  "browser_download_url": "https://example.invalid/setup.exe", "size": 68000000 },
                { "name": "NetControl-Setup-0.6.0.exe.sha256",
                  "browser_download_url": "https://example.invalid/setup.sha256", "size": 80 },
                { "name": "NetControl.Cli.exe",
                  "browser_download_url": "https://example.invalid/cli.exe", "size": 40000000 }
              ]
            }
            """);

        UpdateResult result = await UpdateCheck.RunAsync(AppSettings.Defaults, Running, handler);

        Assert.NotNull(result.Installer);
        Assert.Equal("NetControl-Setup-0.6.0.exe", result.Installer!.Name);
        Assert.Equal("https://example.invalid/setup.sha256", result.Installer.ChecksumUrl!.ToString());

        Assert.NotNull(result.PortableExe);
        Assert.Equal("NetControl.exe", result.PortableExe!.Name);
        Assert.Equal(
            "https://example.invalid/portable.sha256", result.PortableExe.ChecksumUrl!.ToString());

        // And each copy is offered the one it can actually apply.
        Assert.Same(result.Installer, result.AssetFor(InstallKind.Installed));
        Assert.Same(result.PortableExe, result.AssetFor(InstallKind.Portable));
    }

    /// <summary>
    /// An asset published without its checksum still comes back, carrying a null - so the refusal
    /// happens in the downloader, where it can say why, rather than here where the asset would
    /// simply vanish and the dialog would report "this release publishes no installer".
    /// </summary>
    [Fact]
    public async Task CarriesAnAssetThatHasNoChecksumBesideIt()
    {
        var handler = new StubHandler(
            """
            {
              "tag_name": "v0.6.0",
              "assets": [
                { "name": "NetControl-Setup-0.6.0.exe",
                  "browser_download_url": "https://example.invalid/setup.exe", "size": 68000000 }
              ]
            }
            """);

        UpdateResult result = await UpdateCheck.RunAsync(AppSettings.Defaults, Running, handler);

        Assert.NotNull(result.Installer);
        Assert.Null(result.Installer!.ChecksumUrl);
        Assert.Null(result.PortableExe);
    }

    /// <summary>
    /// A mirrored manifest names a version and a page and knows nothing about files, so a site
    /// using one gets the check and not the install. Saying so is the honest answer; guessing where
    /// its mirror keeps the exe would be a download from an invented address.
    /// </summary>
    [Fact]
    public async Task OffersNoFilesWhenTheAnswerCameFromAMirroredManifest()
    {
        UpdateResult result = await UpdateCheck.RunAsync(
            Mirrored(), Running, new StubHandler("""{ "version": "0.6.0" }"""));

        Assert.True(result.IsUpdateAvailable);
        Assert.Null(result.Installer);
        Assert.Null(result.PortableExe);
        Assert.Null(result.AssetFor(InstallKind.Installed));
        Assert.Null(result.AssetFor(InstallKind.Portable));
    }

    /// <summary>
    /// The other shape, which is what a site mirroring builds onto its own intranet publishes. Both
    /// are understood, and which one is in front of the check is decided by the JSON rather than by
    /// the URL it came from.
    /// </summary>
    [Fact]
    public async Task ReportsANewerPublishedBuild()
    {
        var handler = new StubHandler(
            """{ "version": "0.6.0", "url": "https://example.invalid/netcontrol.exe", "notes": "Adds the CLI" }""");

        UpdateResult result = await UpdateCheck.RunAsync(Mirrored(), Running, handler);

        Assert.Equal("https://example.invalid/netcontrol.json", handler.LastUrl);
        Assert.Equal(UpdateAvailability.UpdateAvailable, result.Availability);
        Assert.True(result.IsUpdateAvailable);
        Assert.Equal("0.6.0", result.LatestVersion);
        Assert.Contains("0.6.0 is available", result.StatusText!, StringComparison.Ordinal);
        Assert.Contains("netcontrol.exe", result.StatusText!, StringComparison.Ordinal);
    }

    /// <summary>Already current is silence. A status bar that always says something is one nobody reads.</summary>
    [Fact]
    public async Task SaysNothingWhenAlreadyOnThePublishedBuild()
    {
        UpdateResult result = await UpdateCheck.RunAsync(
            Mirrored(), Running, new StubHandler("""{ "version": "0.5.0" }"""));

        Assert.Equal(UpdateAvailability.Current, result.Availability);
        Assert.Null(result.StatusText);
    }

    /// <summary>
    /// A check that was attempted and did not happen has something to say, unlike one that was
    /// never made. Otherwise a site believes it is current for a year.
    ///
    /// <para>Where that sentence is <em>shown</em> is a separate decision, and not this type's:
    /// startup keeps quiet about it and the Help menu does not. See <c>AppHost</c>.</para>
    /// </summary>
    [Fact]
    public async Task ReportsACheckThatCouldNotBeCompleted()
    {
        UpdateResult result = await UpdateCheck.RunAsync(
            Mirrored(), Running, new StubHandler(status: HttpStatusCode.NotFound));

        Assert.Equal(UpdateAvailability.Failed, result.Availability);
        Assert.NotNull(result.Problem);
        Assert.Contains("Could not check", result.StatusText!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportsAManifestItCouldNotUnderstand()
    {
        UpdateResult malformed = await UpdateCheck.RunAsync(
            Mirrored(), Running, new StubHandler("this is not json"));

        Assert.Equal(UpdateAvailability.Failed, malformed.Availability);

        UpdateResult noVersion = await UpdateCheck.RunAsync(
            Mirrored(), Running, new StubHandler("""{ "notes": "hello" }"""));

        Assert.Equal(UpdateAvailability.Failed, noVersion.Availability);
    }

    /// <summary>
    /// Numbers, not text. <c>0.10.0</c> against <c>0.9.0</c> is the case a string comparison gets
    /// backwards, a commit suffix is which build rather than which version, and a leading <c>v</c>
    /// is a tag convention that must not make every release unreadable.
    /// </summary>
    [Theory]
    [InlineData("0.6.0", "0.5.0", true)]
    [InlineData("0.5.1", "0.5.0", true)]
    [InlineData("0.10.0", "0.9.0", true)]
    [InlineData("1.0", "0.9.9", true)]
    [InlineData("0.6.0+a1b2c3d", "0.5.0", true)]
    [InlineData("v0.6.0", "0.5.0", true)]
    [InlineData("V0.6.0", "0.5.0", true)]
    [InlineData("v0.5.0", "0.5.0", false)]
    [InlineData("0.5.0", "0.5.0", false)]
    [InlineData("0.5.0", "0.6.0", false)]
    [InlineData("0.5.0+a1b2c3d", "0.5.0", false)]
    [InlineData("0.5", "0.5.0", false)]
    [InlineData("not-a-version", "0.5.0", false)]
    [InlineData("version-two", "0.5.0", false)]
    [InlineData("", "0.5.0", false)]
    public void ComparesVersionsAsNumbers(string published, string running, bool expected) =>
        Assert.Equal(expected, UpdateCheck.IsNewer(published, running));

    /// <summary>A site that mirrors builds somewhere its laptops can actually reach.</summary>
    private static AppSettings Mirrored() =>
        new() { UpdateManifestUrl = "https://example.invalid/netcontrol.json" };

    /// <summary>
    /// Answers without a socket. It counts its calls so a test can assert that nothing was
    /// contacted at all, and keeps the request so a test can assert what was asked and how the tool
    /// introduced itself.
    /// </summary>
    private sealed class StubHandler(string body = "{}", HttpStatusCode status = HttpStatusCode.OK)
        : HttpMessageHandler
    {
        public int Calls { get; private set; }

        public string? LastUrl { get; private set; }

        public string? LastUserAgent { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastUrl = request.RequestUri?.ToString();
            LastUserAgent = request.Headers.TryGetValues("User-Agent", out IEnumerable<string>? values)
                ? string.Join(' ', values)
                : null;

            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
