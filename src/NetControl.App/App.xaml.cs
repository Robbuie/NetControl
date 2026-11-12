using System.Windows;
using System.Windows.Threading;
using NetControl.App.Appearance;
using NetControl.App.Composition;
using NetControl.App.Diagnostics;
using NetControl.App.Views;
using NetControl.Core.Diagnostics;
using NetControl.Core.Safety;

namespace NetControl.App;

/// <summary>
/// Application entry point. Builds the object graph, shows the window, and makes sure the socket
/// and the project file are released on the way out.
///
/// <para><b>Nothing here is allowed to fail quietly.</b> This is a <c>WinExe</c>, so there is no
/// console for a stack trace to land in - an exception during startup would otherwise close the
/// process with no window and no message, which is precisely the failure this product exists to
/// remove. Every path out of startup ends in either a window or a dialog that names the cause.</para>
/// </summary>
public partial class App : Application
{
    private AppHost? _host;

    /// <summary>
    /// The diagnostic log. Owned here rather than by <see cref="AppHost"/> so that a graph which
    /// fails while being built - the failure most worth having a file about - is written down.
    /// </summary>
    private TraceLog? _trace;

    /// <summary>The watch on Windows' light/dark mode. Released on exit - it is a static event.</summary>
    private IDisposable? _systemTheme;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Anything that gets past the handlers below still has to say something, so both the UI
        // thread and the process-wide backstop are covered before anything else is built.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        // Open never throws - a folder it cannot write into comes back disabled - so this cannot be
        // the reason the tool fails to start.
        _trace = TraceLog.Open(AppPaths.Logs);
        _trace.Info(BuildInfo.Describe());

        // The engines' own lines - what was sent, what came back, why it failed - go to the same file.
        NetTrace.Log = _trace;

        // Listen-only, the send cap and the verbose trace, as this PC last left them. Applied before
        // anything is built, so a laptop left in listen-only mode cannot transmit even at startup.
        SafetySettings safety = SafetySettingsStore.Load(_trace);
        NetworkGuard.Shared.MaxPacketsPerSecond = safety.MaxPacketsPerSecond;
        NetworkGuard.Shared.ListenOnly = safety.ListenOnly;
        _trace.IsVerbose = safety.VerboseLog;
        _trace.Info($"Network safety: listen only {(safety.ListenOnly ? "on" : "off")}, "
            + $"send cap {(NetworkGuard.Shared.MaxPacketsPerSecond == 0 ? "none" : NetworkGuard.Shared.MaxPacketsPerSecond + "/s")}, "
            + $"verbose log {(safety.VerboseLog ? "on" : "off")}.");

        // A task nobody awaited that failed would otherwise vanish without a word.
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // Before anything is shown, and before the object graph is built. Every window this
        // process opens - including the error dialog three lines down, if the graph fails - is
        // meant to come up on the chosen theme rather than flashing the default first. Neither
        // the load nor the apply can throw: a settings file nobody can read produces the
        // defaults, and an unknown theme name normalises to one that exists.
        Theme.Apply(this, AppearanceStore.Load(_trace));

        // Every window has its own title bar now; these are what its three buttons do.
        WindowChromeCommands.Register();

        // When Windows flips between light and dark, a choice that follows it re-applies. Checked
        // at the moment it fires rather than when subscribing, so turning following on or off in
        // the appearance dialog takes effect without resubscribing.
        _systemTheme = SystemTheme.Watch(() =>
        {
            if (Dispatcher.HasShutdownStarted)
            {
                return;
            }

            Dispatcher.InvokeAsync(() =>
            {
                if (Theme.CurrentChoice.Follow == Theme.FollowWindows)
                {
                    Theme.Apply(this, Theme.CurrentChoice);
                }
            });
        });

        try
        {
            _host = new AppHost(new WpfDispatcher(Dispatcher), _trace);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // The graph builds a SQLite store, inflates the embedded OUI table and enumerates the
            // machine's adapters. Any of those can fail on a locked-down laptop, and "it did not
            // start and did not say why" is not an acceptable answer.
            Report("NetControl could not start.", ex);
            Shutdown(1);
            return;
        }

        // Not assigning Application.MainWindow: WPF sets it to the first window shown, and the
        // bare name here would sit between the MainWindow type and the MainWindow property.
        var shell = new MainWindow { DataContext = _host.ViewModel };
        shell.Show();

        // The previous executable, left beside a portable copy by the last update, and any finished
        // downloads. It happens here rather than at the end of an update because the file being
        // deleted is the one the process doing the updating was running from - which cannot delete
        // itself, and which anything at all can delete a moment later. Silent and best-effort: a
        // leftover file wastes some disk, and a startup that fails over one is the tool not opening.
        UpdateApplier.SweepLeftovers(BuildInfo.ExecutablePath, AppPaths.Updates, _trace);

        // Fire and forget on purpose: the environment checks read the firewall over COM, which on
        // a locked-down laptop takes seconds. The window is already up and says "not checked yet",
        // which is honest, rather than showing nothing while we wait.
        _ = _host.StartAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Before base.OnExit, so the listening socket is closed and the project file's handle is
        // released while the dispatcher is still alive to run any last teardown.
        _host?.Dispose();
        _host = null;

        _systemTheme?.Dispose();
        _systemTheme = null;

        // Last of all, so anything the teardown had to say is in the file before it closes.
        _trace?.Write(NetControl.Core.Persistence.EventSeverity.Info, "Stopped.");
        NetTrace.Log = NullTraceLog.Instance;
        _trace?.Dispose();
        _trace = null;

        base.OnExit(e);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _trace?.Write(NetControl.Core.Persistence.EventSeverity.Error, "A background task failed and nothing was waiting for it.", e.Exception);
        e.SetObserved();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Listen-only stopping a transmission is the switch working, not a crash: say so plainly,
        // without a stack trace, and keep going.
        if (FindBlocked(e.Exception) is { } blocked)
        {
            _trace?.Write(NetControl.Core.Persistence.EventSeverity.Info, blocked.Message);
            MessageBox.Show(
                $"{blocked.Message}{Environment.NewLine}{Environment.NewLine}{blocked.Remediation}",
                "NetControl - listen only",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            e.Handled = true;
            return;
        }

        Report("NetControl hit an error it did not expect.", e.Exception);

        // Handled, so the window survives. A crash mid-commissioning would take the live log with
        // it, and the log is the thing the user is standing there reading.
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            Report("NetControl stopped because of an error on a background thread.", exception);
        }
    }

    /// <summary>
    /// Says what went wrong, twice: to the diagnostic log and to whoever is standing there.
    ///
    /// <para>The file first, and deliberately. A dialog is read by the person at the panel; the
    /// person who has to fix it is usually somewhere else and a week later, and a stack trace that
    /// only ever existed on a screen has been read by nobody. Naming the file in the dialog is what
    /// turns "it crashed" into something that can be sent on.</para>
    /// </summary>
    private static TransmitBlockedException? FindBlocked(Exception? exception) => exception switch
    {
        TransmitBlockedException blocked => blocked,
        AggregateException aggregate => aggregate.InnerExceptions.Select(FindBlocked).FirstOrDefault(b => b is not null),
        null => null,
        _ => FindBlocked(exception.InnerException),
    };

    private void Report(string headline, Exception exception)
    {
        _trace?.Write(NetControl.Core.Persistence.EventSeverity.Error, headline, exception);

        string remediation = exception is NetControl.Core.NetControlException { Remediation: { } next }
            ? Environment.NewLine + Environment.NewLine + next
            : string.Empty;

        string written = _trace?.FilePath is { } path
            ? Environment.NewLine + Environment.NewLine + $"Written to {path}"
            : string.Empty;

        MessageBox.Show(
            $"{headline}{Environment.NewLine}{Environment.NewLine}{exception.GetType().Name}: "
                + $"{exception.Message}{remediation}{written}"
                + $"{Environment.NewLine}{Environment.NewLine}{exception.StackTrace}",
            "NetControl",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}
