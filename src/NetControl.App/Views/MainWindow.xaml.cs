using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
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

    private void OnExit(object sender, RoutedEventArgs e) => Close();
}
