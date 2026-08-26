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
/// unexplained outbound request is a reportable incident - so the default must be that nothing is
/// contacted at all, and the test for that is that the handler is never called.</para>
/// </summary>
public class UpdateCheckTests
{
    private const string Running = "0.5.0";

    /// <summary>
    /// The default on every machine. Nothing is configured, so nothing is contacted - asserted by
    /// the handler counting its calls rather than by the result alone.
    /// </summary>
    [Fact]
    public async Task ContactsNothingWhenNoManifestIsConfigured()
    {
        var handler = new StubHandler("{}");

        UpdateResult result = await UpdateCheck.RunAsync(AppSettings.Defaults, Running, handler);

        Assert.Equal(UpdateAvailability.NotConfigured, result.Availability);
        Assert.Equal(0, handler.Calls);

        // And says nothing in the status bar: not being configured is not news.
        Assert.Null(result.StatusText);
    }

    [Fact]
    public async Task ReportsANewerPublishedBuild()
    {
        var handler = new StubHandler(
            """{ "version": "0.6.0", "url": "https://example.invalid/netcontrol.exe", "notes": "Adds the CLI" }""");

        UpdateResult result = await UpdateCheck.RunAsync(Configured(), Running, handler);

        Assert.Equal(UpdateAvailability.UpdateAvailable, result.Availability);
        Assert.True(result.IsUpdateAvailable);
        Assert.Equal("0.6.0", result.LatestVersion);
        Assert.Contains("0.6.0 is available", result.StatusText!, StringComparison.Ordinal);
        Assert.Contains("netcontrol.exe", result.StatusText!, StringComparison.Ordinal);
    }

    /// <summary>Already current is also silence. A status bar that always says something is one nobody reads.</summary>
    [Fact]
    public async Task SaysNothingWhenAlreadyOnThePublishedBuild()
    {
        UpdateResult result = await UpdateCheck.RunAsync(
            Configured(), Running, new StubHandler("""{ "version": "0.5.0" }"""));

        Assert.Equal(UpdateAvailability.Current, result.Availability);
        Assert.Null(result.StatusText);
    }

    /// <summary>
    /// A check that was asked for and did not happen is reported, unlike one that was never
    /// configured. Otherwise a site turns this on and believes it is current for a year.
    /// </summary>
    [Fact]
    public async Task ReportsACheckThatCouldNotBeCompleted()
    {
        UpdateResult result = await UpdateCheck.RunAsync(
            Configured(), Running, new StubHandler(status: HttpStatusCode.NotFound));

        Assert.Equal(UpdateAvailability.Failed, result.Availability);
        Assert.NotNull(result.Problem);
        Assert.Contains("Could not check", result.StatusText!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportsAManifestItCouldNotUnderstand()
    {
        UpdateResult malformed = await UpdateCheck.RunAsync(
            Configured(), Running, new StubHandler("this is not json"));

        Assert.Equal(UpdateAvailability.Failed, malformed.Availability);

        UpdateResult noVersion = await UpdateCheck.RunAsync(
            Configured(), Running, new StubHandler("""{ "notes": "hello" }"""));

        Assert.Equal(UpdateAvailability.Failed, noVersion.Availability);
    }

    /// <summary>
    /// Numbers, not text. <c>0.10.0</c> against <c>0.9.0</c> is the case a string comparison gets
    /// backwards, and a commit suffix is which build rather than which version.
    /// </summary>
    [Theory]
    [InlineData("0.6.0", "0.5.0", true)]
    [InlineData("0.5.1", "0.5.0", true)]
    [InlineData("0.10.0", "0.9.0", true)]
    [InlineData("1.0", "0.9.9", true)]
    [InlineData("0.6.0+a1b2c3d", "0.5.0", true)]
    [InlineData("0.5.0", "0.5.0", false)]
    [InlineData("0.5.0", "0.6.0", false)]
    [InlineData("0.5.0+a1b2c3d", "0.5.0", false)]
    [InlineData("0.5", "0.5.0", false)]
    [InlineData("not-a-version", "0.5.0", false)]
    [InlineData("", "0.5.0", false)]
    public void ComparesVersionsAsNumbers(string published, string running, bool expected) =>
        Assert.Equal(expected, UpdateCheck.IsNewer(published, running));

    private static AppSettings Configured() =>
        new() { UpdateManifestUrl = "https://example.invalid/netcontrol.json" };

    /// <summary>
    /// Answers without a socket, and counts its calls so a test can assert that nothing was
    /// contacted at all.
    /// </summary>
    private sealed class StubHandler(string body = "{}", HttpStatusCode status = HttpStatusCode.OK)
        : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;

            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
