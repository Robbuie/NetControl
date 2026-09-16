using NetControl.App.Diagnostics;
using NetControl.App.Serving;
using NetControl.App.ViewModels;
using NetControl.Core.Dhcp;
using NetControl.Core.Diagnostics;
using NetControl.Core.Discovery;
using NetControl.Core.Enip;
using NetControl.Core.Interfaces;
using NetControl.Core.Oui;
using NetControl.Core.Persistence;

namespace NetControl.App.Composition;

/// <summary>
/// Builds the object graph and owns everything with a lifetime. There is no container: the graph
/// is a dozen objects wired once, and a container would hide the one thing worth being able to
/// read off the page - what talks to what.
///
/// <para>The project starts in memory. The app has to be usable before anyone has saved a file,
/// and an event log that only begins recording once you pick a filename misses the first ten
/// minutes - which is exactly when the "why is nothing arriving" events happen.</para>
/// </summary>
public sealed class AppHost : IDisposable
{
    private readonly NicMonitor _nics;
    private readonly ITraceLog _trace;
    private bool _disposed;

    /// <param name="dispatcher">How Core's background events reach the UI thread.</param>
    /// <param name="trace">
    /// The diagnostic log, owned by the caller. Passed in rather than created here because the
    /// graph below is the thing most likely to fail on a locked-down laptop, and a log opened
    /// inside this constructor would be lost along with it.
    /// </param>
    public AppHost(IUiDispatcher dispatcher, ITraceLog? trace = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);

        _trace = trace ?? NullTraceLog.Instance;
        _nics = new NicMonitor();

        var policy = new StaticMapPolicy();
        var controller = new ServerController(_nics, policy);
        ProjectStore project = ProjectStore.CreateInMemory();

        // Active discovery. Built here rather than inside the view model so that the scanner, the
        // ARP table and the vendor lookup are visible as three separate things on one page - and so
        // a test can hand the view model a discovery whose scanner never touches a socket. The
        // options are Core's defaults: one broadcast, a two-second window, and the same reply send
        // mode question the DHCP server has.
        var discovery = new DeviceDiscovery(
            new IdentityScanner(new ScanOptions { SendMode = ReplySendMode.PerSocketBind }),
            new ArpTable(),
            OuiDatabase.Bundled);

        ViewModel = new MainViewModel(
            dispatcher,
            _nics,
            new Preflight(),
            controller,
            policy,
            OuiDatabase.Bundled,
            project,
            discovery: discovery,

            // Only the real application stamps the project file with a build. A test that
            // constructs a view model wants an event log holding exactly what its own actions put
            // there, and one that starts with a line about a version is a test that has to know
            // about versions.
            buildStamp: BuildInfo.Stamp());

        // Raised on a background thread by design, so the view model marshals. Subscribing before
        // Start means the first inventory arrives through the same path as every later one.
        _nics.NicsChanged += OnNicsChanged;
        _nics.Start();
    }

    public MainViewModel ViewModel { get; }

    /// <summary>The diagnostic log this graph was given. Never null; may write nothing.</summary>
    public ITraceLog Trace => _trace;

    /// <summary>
    /// Kicks off the checks that need to run before anything can arrive. Deliberately not done in
    /// the constructor: reading the firewall over COM can take seconds, and a window that has not
    /// appeared yet cannot tell the user why it is waiting.
    /// </summary>
    public async Task StartAsync()
    {
        ViewModel.RefreshAdapters();
        await ViewModel.RefreshEnvironmentCommand.ExecuteAsync(null).ConfigureAwait(true);
        await CheckForUpdatesAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Asks whether a newer build has been published - the repository's latest release by default,
    /// a site's own manifest if one is configured, and nothing at all if <c>checkForUpdates</c> is
    /// false. See <see cref="UpdateCheck"/>.
    ///
    /// <para>Last in the startup sequence and awaited only so failures are logged in order. It
    /// cannot throw, and nothing waits on the answer: the window is up and usable before this runs.
    /// </para>
    ///
    /// <para><b>Only an available update reaches the status bar here.</b> A failed check does have
    /// something to say, but not on this path: on a plant segment with no route out it fails at
    /// every launch, and a warning that is always present is one people stop reading - which is the
    /// same argument that keeps both quiet states quiet. The failure is in the diagnostic log, and
    /// <b>Help &gt; Check for updates</b> reports it to somebody who is waiting for an answer.</para>
    /// </summary>
    private async Task CheckForUpdatesAsync()
    {
        AppSettings settings = AppSettings.Load(AppPaths.SettingsFile, _trace);

        UpdateResult result = await UpdateCheck
            .RunAsync(settings, BuildInfo.Version, trace: _trace)
            .ConfigureAwait(true);

        ViewModel.UpdateStatus = result.IsUpdateAvailable ? result.StatusText : null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _trace.Info("Releasing the listener and the project file.");

        _nics.NicsChanged -= OnNicsChanged;
        ViewModel.Dispose();
        _nics.Dispose();
    }

    private void OnNicsChanged(object? sender, NicsChangedEventArgs e) => ViewModel.RefreshAdapters();
}
