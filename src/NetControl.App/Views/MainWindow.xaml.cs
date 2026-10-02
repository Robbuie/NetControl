// UseWPF drops System.IO from the implicit usings, because WPF ships its own Path. The Help menu
// touches Directory and IOException, so this file asks for it back by name - see CLAUDE.md.
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using NetControl.App.Composition;
using NetControl.App.Diagnostics;
using NetControl.App.ViewModels;

namespace NetControl.App.Views;

/// <summary>
/// The shell. There is deliberately almost nothing here: putting a file dialog on the screen, and
/// turning a gesture on a row into a call on the view model. Both are view concerns, and the first
/// is the one thing a view model genuinely cannot do without dragging a window into a unit test.
/// </summary>
public partial class MainWindow : Window
{
    private const string ProjectExtension = ".netcproj";

    private const string ProjectFilter = "NetControl project (*.netcproj)|*.netcproj|All files (*.*)|*.*";

    private const string PlanFilter = "Plan (*.csv)|*.csv|All files (*.*)|*.*";

    /// <summary>Guards the Help menu's update check against a second press while one is in flight.</summary>
    private bool _checkingForUpdates;

    public MainWindow() => InitializeComponent();

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void OnOpenProject(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Open project",
            Filter = ProjectFilter,
            CheckFileExists = false,
            DefaultExt = ProjectExtension,
        };

        if (dialog.ShowDialog(this) == true)
        {
            viewModel.OpenProject(dialog.FileName);
        }
    }

    private void OnNewProject(object sender, RoutedEventArgs e) => ViewModel?.NewProject();

    private void OnSaveProjectAs(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Save project as",
            Filter = ProjectFilter,
            DefaultExt = ProjectExtension,
            AddExtension = true,

            // Core refuses to write over an existing project file, because one is somebody's
            // commissioning record. This dialog is where that decision belongs, so getting past it
            // is what authorises the replacement below.
            OverwritePrompt = true,
        };

        if (dialog.ShowDialog(this) == true)
        {
            viewModel.SaveProjectAs(dialog.FileName, replaceExisting: true);
        }
    }

    private void OnImportPlan(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Import plan from CSV",
            Filter = PlanFilter,
            DefaultExt = ".csv",
        };

        if (dialog.ShowDialog(this) == true)
        {
            viewModel.ImportPlan(dialog.FileName);
        }
    }

    /// <summary>
    /// Unlike Save as, this one may replace a file without asking anything further. A CSV is a
    /// working copy of the plan, not the commissioning record - that is the project file - so the
    /// dialog's own overwrite prompt is the whole of the confirmation needed.
    /// </summary>
    private void OnExportPlan(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Export plan to CSV",
            Filter = PlanFilter,
            DefaultExt = ".csv",
            AddExtension = true,
            OverwritePrompt = true,
        };

        if (dialog.ShowDialog(this) == true)
        {
            viewModel.ExportPlan(dialog.FileName);
        }
    }

    /// <summary>
    /// Double-click a row of the live request log to plan the device that sent it. This is the
    /// gesture the tool is built around - watch a device ask, plan it, and it is served on its next
    /// retransmit - so it is the cheapest one available.
    /// </summary>
    /// <remarks>
    /// Two handlers rather than one taking <see cref="RoutedEventArgs"/>, because
    /// <c>MouseDoubleClick</c> is a <see cref="MouseButtonEventHandler"/> and <c>Click</c> is a
    /// <see cref="RoutedEventHandler"/>. One method could satisfy both through delegate
    /// contravariance, but an <c>EventSetter</c> binds its handler by name at runtime, and a
    /// mismatch there is a missing gesture at the bench rather than a build error here.
    /// </remarks>
    private void OnLogRowDoubleClicked(object sender, MouseButtonEventArgs e)
    {
        Plan(sender);

        // A double-click on a row is not also a request to do whatever the ListView would have
        // done with it.
        e.Handled = true;
    }

    private void OnAddToPlan(object sender, RoutedEventArgs e) => Plan(sender);

    /// <summary>
    /// The same gesture on a scan result. Unlike a log row this one arrives with an address on it -
    /// the one the device is already holding - so the plan row lands complete. See
    /// <see cref="MainViewModel.PlanFromScan"/>.
    /// </summary>
    private void OnScanRowDoubleClicked(object sender, MouseButtonEventArgs e)
    {
        PlanScanResult(sender);
        e.Handled = true;
    }

    private void OnAddScanResultToPlan(object sender, RoutedEventArgs e) => PlanScanResult(sender);

    private void PlanScanResult(object sender)
    {
        if (ViewModel is { } viewModel && ScanResultFrom(sender) is { } result)
        {
            viewModel.PlanFromScan(result);
        }
    }

    /// <summary>
    /// The scan row a gesture was made on, resolved the same way a log row is - through the context
    /// menu's placement target for a menu item, and through the row itself for a double-click.
    /// </summary>
    private static ScanResultViewModel? ScanResultFrom(object sender) => sender switch
    {
        MenuItem
        {
            Parent: ContextMenu { PlacementTarget: FrameworkElement { DataContext: ScanResultViewModel row } },
        } => row,
        FrameworkElement { DataContext: ScanResultViewModel row } => row,
        _ => null,
    };

    /// <summary>
    /// Hands the log row behind <paramref name="sender"/> to the view model.
    ///
    /// <para>A row with no MAC - a notice, a fault, the tool talking about itself - is passed on
    /// rather than dropped here: what that means is the view model's to say, and a gesture that
    /// silently does nothing is a gesture people repeat.</para>
    /// </summary>
    private void Plan(object sender)
    {
        if (ViewModel is { } viewModel && EntryFrom(sender) is { } entry)
        {
            viewModel.PlanFromLog(entry);
        }
    }

    /// <summary>
    /// The entry a gesture was made on.
    ///
    /// <para>A menu item is resolved through the context menu's placement target, and deliberately
    /// not through its own data context. A <c>ContextMenu</c> declared in a style is one instance
    /// shared by every row, and it is the placement target - the row it was actually opened on -
    /// that is unambiguous. A double-click arrives on the row itself, so it needs none of that.</para>
    /// </summary>
    private static LogEntryViewModel? EntryFrom(object sender) => sender switch
    {
        MenuItem { Parent: ContextMenu { PlacementTarget: FrameworkElement { DataContext: LogEntryViewModel entry } } }
            => entry,
        FrameworkElement { DataContext: LogEntryViewModel entry } => entry,
        _ => null,
    };

    /// <summary>
    /// Hands the selected device back to a BOOTP server, after asking.
    ///
    /// <para>The confirmation is here rather than in the view model for the ordinary reason - a
    /// view model that puts a dialog on the screen cannot be unit tested - and it is a confirmation
    /// at all because this is the one operation in the tool that <em>removes</em> a device's hold on
    /// its address. Everything else either observes, or makes an address more permanent.</para>
    /// </summary>
    private void OnEnableBootp(object sender, RoutedEventArgs e) =>
        HandBack("BOOTP", ViewModel?.Plan.EnableBootpCommand);

    private void OnEnableDhcp(object sender, RoutedEventArgs e) =>
        HandBack("DHCP", ViewModel?.Plan.EnableDhcpCommand);

    /// <summary>
    /// Names the device and the consequence, then runs the command. The consequence is the part
    /// worth writing carefully: nothing appears to happen on the panel, and the device keeps working
    /// at the address it has - right up until the next power cycle, which may be weeks away and will
    /// not be attended by anybody who remembers this dialog.
    /// </summary>
    private void HandBack(string protocol, ICommand? command)
    {
        if (command is null
            || ViewModel?.Plan.SelectedRow is not { } row
            || !command.CanExecute(null))
        {
            return;
        }

        string device = row.Mac.IsEmpty ? "the selected device" : row.Mac.ToString();

        MessageBoxResult answer = MessageBox.Show(
            this,
            $"Turn {protocol} back on for {device}?"
                + Environment.NewLine + Environment.NewLine
                + "Only Configuration Control is written - the addresses the device is holding are left "
                + "exactly as they are, and it keeps working at the address it has now."
                + Environment.NewLine + Environment.NewLine
                + $"But it stops owning that address. At its next power cycle it will ask for one over "
                + $"{protocol}, and it will not come back until something answers."
                + Environment.NewLine + Environment.NewLine
                + "This is the opposite of Set static.",
            $"Enable {protocol}",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,

            // No is the default, so a return keypress aimed at something else cannot authorise it.
            MessageBoxResult.No);

        if (answer == MessageBoxResult.Yes)
        {
            command.Execute(null);
        }
    }

    /// <summary>
    /// Theme, accent and density. The dialog previews as it is used and saves on OK, so there is
    /// nothing to do with the result here.
    /// </summary>
    private void OnAppearance(object sender, RoutedEventArgs e) =>
        new AppearanceWindow { Owner = this }.ShowDialog();

    /// <summary>
    /// The update check somebody asked for, which unlike the one at startup always answers.
    ///
    /// <para>The startup check is silent unless there is a newer build, because on a plant segment
    /// with no route out it fails at every launch and a warning that is always there is one nobody
    /// reads. This one is different: a person pressed it and is standing there waiting, so all four
    /// outcomes get a sentence - including "switched off on this machine", which is otherwise
    /// indistinguishable from "checked, and you are current".</para>
    ///
    /// <para>When there is something newer this opens <see cref="UpdateWindow"/>, which says what
    /// it is about to do to this copy before it does it. Nothing is fetched by the check itself,
    /// and nothing is ever run that has not been checked against the checksum published with it.
    /// </para>
    /// </summary>
    private async void OnCheckForUpdates(object sender, RoutedEventArgs e)
    {
        // A second press while the first is in flight would produce two dialogs, and the five-second
        // timeout is long enough for somebody to try.
        if (_checkingForUpdates)
        {
            return;
        }

        _checkingForUpdates = true;

        try
        {
            // Read afresh rather than caching what startup read: somebody who has just edited
            // settings.json to point at a mirror is very likely the person pressing this.
            AppSettings settings = AppSettings.Load(AppPaths.SettingsFile);

            // RunAsync does not throw - a failure is one of its four results - so there is nothing
            // to catch here and no way for this handler to take the window down.
            UpdateResult result = await UpdateCheck.RunAsync(settings, BuildInfo.Version);

            if (ViewModel is { } viewModel)
            {
                viewModel.UpdateStatus = result.StatusText;
            }

            if (result.IsUpdateAvailable)
            {
                ShowUpdateDialog(result);
                return;
            }

            string message = result.Availability switch
            {
                UpdateAvailability.Current =>
                    $"{BuildInfo.Version} is the newest published build.",

                UpdateAvailability.TurnedOff =>
                    "The update check is switched off on this machine, so nothing was contacted."
                        + Environment.NewLine + Environment.NewLine
                        + $"It is \"checkForUpdates\": false in {AppPaths.SettingsFile}."
                        + Environment.NewLine + Environment.NewLine
                        + $"Published builds are at {BuildInfo.ReleasesPage}",

                _ =>
                    $"Could not check: {result.Problem}"
                        + Environment.NewLine + Environment.NewLine
                        + "That is the usual answer on a segment with no route out, and it says "
                        + "nothing about whether a newer build exists.",
            };

            MessageBox.Show(
                this,
                message,
                "Check for updates",
                MessageBoxButton.OK,
                result.Availability == UpdateAvailability.Failed
                    ? MessageBoxImage.Warning
                    : MessageBoxImage.Information);
        }
        finally
        {
            _checkingForUpdates = false;
        }
    }

    /// <summary>
    /// The status-bar line, which only ever appears when a newer build exists. It re-runs the check
    /// rather than reusing what startup found: the line may have been sitting there since this
    /// morning, and the release it names may have been replaced since.
    /// </summary>
    private void OnUpdateStatusClicked(object sender, MouseButtonEventArgs e)
    {
        OnCheckForUpdates(sender, e);
        e.Handled = true;
    }

    private void OnOpenReleases(object sender, RoutedEventArgs e) =>
        Shell.Open(this, BuildInfo.ReleasesPage);

    /// <summary>
    /// Opens the folder holding the diagnostic log.
    ///
    /// <para>Created if it is not there, because the alternative is Explorer reporting a missing
    /// folder to somebody who has just been asked to send their log in. An empty folder at least
    /// says where to look next time.</para>
    /// </summary>
    private void OnOpenLogFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Logs);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            MessageBox.Show(
                this,
                $"{AppPaths.Logs} could not be opened: {ex.Message}",
                "Diagnostic log",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            return;
        }

        Shell.Open(this, AppPaths.Logs);
    }

    /// <summary>
    /// Which build this is, on the machine it is on. The same string that heads the diagnostic log
    /// and stamps every project file, so a screenshot of this box and a commissioning record can be
    /// matched to each other.
    /// </summary>
    private void OnAbout(object sender, RoutedEventArgs e) =>
        MessageBox.Show(
            this,
            BuildInfo.Describe()
                + Environment.NewLine + Environment.NewLine
                + (BuildInfo.ExecutablePath is { } exe ? exe + Environment.NewLine : string.Empty)
                + AppPaths.Data
                + Environment.NewLine + Environment.NewLine
                + BuildInfo.ReleasesPage
                + Environment.NewLine + Environment.NewLine
                + "Built from public specifications: RFC 951, RFC 1542, RFC 2131/2132, and ODVA's "
                + "published CIP and EtherNet/IP documentation.",
            "About NetControl",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

    /// <summary>
    /// Opens the download-and-install dialog, having first worked out what this copy is and
    /// whether now is a moment it may replace itself.
    /// </summary>
    private void ShowUpdateDialog(UpdateResult result)
    {
        var dialog = new UpdateWindow(
            result,
            InstallLocation.Describe(BuildInfo.ExecutablePath),
            BlockedReason())
        {
            Owner = this,
        };

        dialog.ShowDialog();

        if (dialog.StatusAfterClose is { } status && ViewModel is { } viewModel)
        {
            viewModel.UpdateStatus = status;
        }

        if (dialog.ShouldShutdown)
        {
            // The replacement is already on its way up. Close the ordinary way so the listener and
            // the project file are released by App.OnExit rather than by the process ending.
            Application.Current.Shutdown();
        }
    }

    /// <summary>
    /// Why this is not a moment to replace the application, or null.
    ///
    /// <para>Both answers are about the same thing: an update ends with this process gone, and
    /// there are two states in which that is not merely inconvenient. A running listener is a
    /// socket somebody is watching for a request that may be the one that explains everything, and
    /// on a real segment it may not come again for a shift. A commissioning operation in flight is
    /// a write to a live device that has been sent and not yet read back - which is precisely the
    /// window in which the tool's own record is the only account of what was done.</para>
    ///
    /// <para>Neither is a reason to hide the dialog. It says what to stop and why, because "the
    /// button is greyed out" is how somebody ends up believing the update is broken.</para>
    /// </summary>
    private string? BlockedReason()
    {
        if (ViewModel is not { } viewModel)
        {
            return null;
        }

        if (viewModel.Plan.IsCommissioning)
        {
            return "A device is being commissioned right now. Updating would close NetControl in "
                + "the middle of a write to live equipment, and the record of what was sent would "
                + "stop at whatever had been written down so far. Let it finish first.";
        }

        if (viewModel.IsRunning)
        {
            return "The listener is running. Updating closes NetControl, and anything that asks "
                + "for an address while it is closed is a request nobody sees - which on a real "
                + "segment may not come again for hours. Stop the listener first.";
        }

        return null;
    }

    /// <summary>
    /// Picks the TFTP server's root folder for the TFTP tab, then checks it straight away - picking a
    /// folder is the moment somebody wants to know whether a backup can land in it.
    /// </summary>
    private void OnBrowseTftpRoot(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = "TFTP server root folder",
            Multiselect = false,
        };

        string current = viewModel.Tftp.RootFolder;
        if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
        {
            dialog.InitialDirectory = current;
        }

        if (dialog.ShowDialog(this) == true)
        {
            viewModel.Tftp.RootFolder = dialog.FolderName;
            viewModel.Tftp.CheckRootCommand.Execute(null);
        }
    }

    private void OnExit(object sender, RoutedEventArgs e) => Close();
}
