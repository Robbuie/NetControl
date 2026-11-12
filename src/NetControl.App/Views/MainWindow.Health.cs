using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;

namespace NetControl.App.Views;

/// <summary>
/// The window's half of network health, network safety and the diagnostic log: opening the two
/// modeless windows (one of each), the support bundle's file dialog, and the status-bar line.
/// </summary>
public partial class MainWindow
{
    private NetworkActivityWindow? _networkActivity;
    private LogViewerWindow? _logViewer;
    private DispatcherTimer? _activityTimer;

    /// <summary>
    /// The status-bar packet count, refreshed once a second rather than per packet: a scan sends a
    /// thousand, and a line that redrew for each would be the busiest thing on screen.
    /// </summary>
    private void StartActivityTimer()
    {
        _activityTimer ??= new DispatcherTimer(
            TimeSpan.FromSeconds(1),
            DispatcherPriority.Background,
            (_, _) => ViewModel?.RefreshNetworkActivity(),
            Dispatcher);
        _activityTimer.Start();
        Closed += (_, _) => _activityTimer?.Stop();
    }

    private void OnShowHealthTab(object sender, RoutedEventArgs e) => HealthTab.IsSelected = true;

    private void OnNetworkActivityClicked(object sender, MouseButtonEventArgs e) => ShowNetworkActivity();

    private void OnNetworkActivity(object sender, RoutedEventArgs e) => ShowNetworkActivity();

    private void ShowNetworkActivity()
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        if (_networkActivity is { IsLoaded: true })
        {
            _networkActivity.Activate();
            return;
        }

        _networkActivity = new NetworkActivityWindow(viewModel) { Owner = this };
        _networkActivity.Closed += (_, _) => _networkActivity = null;
        _networkActivity.Show();
    }

    private void OnLogViewer(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        if (_logViewer is { IsLoaded: true })
        {
            _logViewer.Activate();
            return;
        }

        _logViewer = new LogViewerWindow(viewModel) { Owner = this };
        _logViewer.Closed += (_, _) => _logViewer = null;
        _logViewer.Show();
    }

    private void OnCopyFindings(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        try
        {
            Clipboard.SetText(viewModel.Health.FindingsText());
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Another program is holding the clipboard. Press Copy again.
        }
    }

    private void OnExportSupportBundle(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Export support bundle",
            Filter = "Zip file (*.zip)|*.zip|All files (*.*)|*.*",
            DefaultExt = ".zip",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = $"NetControl support {DateTime.Now:yyyy-MM-dd HHmm}.zip",
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        if (viewModel.ExportSupportBundle(dialog.FileName) is { } problem)
        {
            MessageBox.Show(this, problem, "Support bundle", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        MessageBoxResult open = MessageBox.Show(
            this,
            $"Written to {dialog.FileName}.{Environment.NewLine}{Environment.NewLine}It holds the diagnostic logs, this PC's "
                + "adapters, the sent-packet list, the last troubleshoot and the project's event record - not the plan itself. "
                + "Show it in its folder?",
            "Support bundle",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);

        if (open == MessageBoxResult.Yes)
        {
            Shell.Open(this, System.IO.Path.GetDirectoryName(dialog.FileName) ?? dialog.FileName);
        }
    }
}
