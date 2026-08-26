using NetControl.App.Diagnostics;
using NetControl.App.Serving;
using NetControl.App.ViewModels;
using NetControl.Core.Dhcp;
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
    private bool _disposed;

    public AppHost(IUiDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);

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
            discovery: discovery);

        // Raised on a background thread by design, so the view model marshals. Subscribing before
        // Start means the first inventory arrives through the same path as every later one.
        _nics.NicsChanged += OnNicsChanged;
        _nics.Start();
    }

    public MainViewModel ViewModel { get; }

    /// <summary>
    /// Kicks off the checks that need to run before anything can arrive. Deliberately not done in
    /// the constructor: reading the firewall over COM can take seconds, and a window that has not
    /// appeared yet cannot tell the user why it is waiting.
    /// </summary>
    public Task StartAsync()
    {
        ViewModel.RefreshAdapters();
        return ViewModel.RefreshEnvironmentCommand.ExecuteAsync(null);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _nics.NicsChanged -= OnNicsChanged;
        ViewModel.Dispose();
        _nics.Dispose();
    }

    private void OnNicsChanged(object? sender, NicsChangedEventArgs e) => ViewModel.RefreshAdapters();
}
