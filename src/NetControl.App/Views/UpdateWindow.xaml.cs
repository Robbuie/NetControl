using System.Windows;
using NetControl.App.Composition;
using NetControl.App.Diagnostics;
using NetControl.Core.Diagnostics;

namespace NetControl.App.Views;

/// <summary>
/// Downloads a published build, proves it is the published build, and puts it in place of this one.
///
/// <para>Only ever opened when there is genuinely something newer. The other three outcomes of a
/// check are a sentence each and belong in a message box.</para>
///
/// <para><b>The two ways out of here are not symmetrical, and that is the thing to hold on to when
/// reading this file.</b> A portable copy swaps its own executable and then this window shuts the
/// application down. An installed copy hands off to setup, which closes the application itself
/// through the Restart Manager - so on that path this window closes immediately and gets out of the
/// way, because a modal dialog is one more window Windows has to ask nicely to close, and being
/// force-terminated mid-write is the one outcome a tool holding a commissioning record must not
/// have.</para>
/// </summary>
public partial class UpdateWindow : Window
{
    private readonly UpdateResult _result;
    private readonly UpdateAsset? _asset;
    private readonly InstallKind _kind;
    private readonly ITraceLog _trace;

    private CancellationTokenSource? _download;
    private bool _running;

    public UpdateWindow(UpdateResult result, InstallKind kind, string? blockedReason, ITraceLog? trace = null)
    {
        ArgumentNullException.ThrowIfNull(result);

        InitializeComponent();

        _result = result;
        _kind = kind;
        _trace = trace ?? NullTraceLog.Instance;
        _asset = result.AssetFor(kind);

        Headline.Text = $"NetControl {result.LatestVersion} is available.";

        Detail.Text = $"This is {BuildInfo.Version}."
            + (result.Notes is null ? string.Empty : Environment.NewLine + result.Notes);

        Describe(blockedReason);
    }

    /// <summary>
    /// True when the caller must shut the application down: the executable has already been
    /// replaced and its replacement is starting.
    /// </summary>
    public bool ShouldShutdown { get; private set; }

    /// <summary>
    /// What the status bar should say once this closes, or null. Used on the installer path, where
    /// this window goes away while setup is still working.
    /// </summary>
    public string? StatusAfterClose { get; private set; }

    /// <summary>
    /// Says exactly what pressing Install will do to this copy, or why it will not be doing
    /// anything. Three reasons it might not: something is running that must not be interrupted, the
    /// release published no file this copy can use, or the answer came from a mirrored manifest,
    /// which names a version and knows nothing about files.
    /// </summary>
    private void Describe(string? blockedReason)
    {
        if (blockedReason is not null)
        {
            Method.Text = blockedReason;
            InstallButton.IsEnabled = false;
            return;
        }

        if (_asset is null)
        {
            Method.Text = _kind == InstallKind.Installed
                ? "This release publishes no installer, so there is nothing to run here. The "
                    + "releases page has whatever it does publish."
                : "This release publishes no loose NetControl.exe, so this portable copy cannot "
                    + "replace itself from it. The releases page has whatever it does publish.";

            InstallButton.IsEnabled = false;
            return;
        }

        Method.Text = _kind == InstallKind.Installed
            ? $"NetControl will download {_asset.Name} ({_asset.SizeText}), check it against the "
                + "SHA256 published with it, and run it silently. No administrator prompt: the "
                + "install is per-user. NetControl closes and reopens on the new version."
            : $"NetControl will download {_asset.Name} ({_asset.SizeText}), check it against the "
                + "SHA256 published with it, and put it in place of the copy you are running. "
                + "NetControl closes and reopens on the new version, and the build you are running "
                + "now is kept beside it until the next start in case it has to go back.";
    }

    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        if (_running || _asset is null)
        {
            return;
        }

        _running = true;
        InstallButton.IsEnabled = false;
        PageButton.IsEnabled = false;
        CloseButton.Content = "Cancel";
        Problem.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressNote.Text = $"Downloading {_asset.Name}...";

        // Progress created here, on the UI thread, so its callback comes back here without anything
        // in the downloader having to know a window exists.
        var progress = new Progress<double>(fraction => Bar.Value = fraction);

        _download = new CancellationTokenSource();

        try
        {
            UpdateDownload download = await UpdateDownloader.DownloadAsync(
                _asset, AppPaths.Updates, progress, trace: _trace,
                cancellationToken: _download.Token);

            if (!download.IsVerified)
            {
                Stop(download.Problem ?? "The download did not complete.");
                return;
            }

            ProgressNote.Text = "Verified. Applying...";
            Bar.IsIndeterminate = true;

            Apply(download);
        }
        finally
        {
            _download?.Dispose();
            _download = null;
        }
    }

    private void Apply(UpdateDownload download)
    {
        if (_kind == InstallKind.Portable)
        {
            UpdateApplyResult swap = UpdateApplier.SwapPortable(
                download, BuildInfo.ExecutablePath, _trace);

            if (swap.Outcome != UpdateApplyOutcome.Restarting)
            {
                Stop(swap.Problem ?? "The update could not be applied.");
                return;
            }

            // The replacement is already starting. Close, and let the caller shut this one down -
            // the two overlap for about a second, which is why an update is refused while the
            // listener is up.
            ShouldShutdown = true;
            Close();
            return;
        }

        UpdateApplyResult install = UpdateApplier.RunInstaller(download, _trace);

        if (install.Outcome != UpdateApplyOutcome.InstallerRunning)
        {
            Stop(install.Problem ?? "The installer could not be started.");
            return;
        }

        // Deliberately not shutting down, and deliberately not staying on screen either. Setup
        // closes this process itself, the ordinary way, and it should have as few windows to ask
        // about as possible while it does.
        StatusAfterClose = $"Installing {_result.LatestVersion} - NetControl will close and reopen.";
        Close();
    }

    /// <summary>Puts the dialog back the way it was, with the reason on it. Nothing was replaced.</summary>
    private void Stop(string problem)
    {
        _running = false;
        Bar.IsIndeterminate = false;
        ProgressPanel.Visibility = Visibility.Collapsed;
        Problem.Text = problem;
        Problem.Visibility = Visibility.Visible;
        CloseButton.Content = "Close";
        PageButton.IsEnabled = true;

        // Enabled again on purpose: the two most common reasons to land here - a link that dropped,
        // and a proxy that answered with something else - are both worth one more press.
        InstallButton.IsEnabled = _asset is not null;
    }

    private void OnOpenReleases(object sender, RoutedEventArgs e) =>
        Shell.Open(this, _result.DownloadUrl ?? BuildInfo.ReleasesPage);

    private void OnClose(object sender, RoutedEventArgs e)
    {
        // While a download is running this button says Cancel, and cancelling is all it does: the
        // window stays so the reason can be read.
        if (_running && _download is { } cancel)
        {
            cancel.Cancel();
            return;
        }

        Close();
    }
}
