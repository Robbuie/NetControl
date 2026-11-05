using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using NetControl.App.ViewModels;

namespace NetControl.App.Views;

/// <summary>
/// The plan grid's gestures: multi-row selection, the keys, the clipboard, and the filter's view.
/// Each one turns into a single call on <see cref="DeviceGridViewModel"/>, where the rule lives.
/// </summary>
public partial class MainWindow
{
    /// <summary>Hands the grid's whole selection to the view model, which only binds the current row.</summary>
    private void OnPlanSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is { } viewModel)
        {
            viewModel.Plan.SetSelection(PlanGrid.SelectedItems.OfType<DeviceRowViewModel>());
        }
    }

    /// <summary>
    /// The plan's keys. Skipped while a cell is being typed into - there Delete deletes a character and
    /// Ctrl+C copies text, as anybody expects - which is why this is a key handler that looks where the
    /// key went rather than a set of input bindings that would also fire inside the cell's text box.
    /// </summary>
    private void OnPlanGridKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel is not { } viewModel || e.OriginalSource is TextBox)
        {
            return;
        }

        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        DeviceGridViewModel plan = viewModel.Plan;

        switch (e.Key)
        {
            case Key.Delete when !ctrl:
                Run(plan.RemoveDeviceCommand);
                break;
            case Key.C when ctrl:
                CopyPlanRows();
                break;
            case Key.V when ctrl:
                PastePlanRows();
                break;
            case Key.Z when ctrl:
                Run(plan.UndoRemoveCommand);
                break;
            case Key.D when ctrl:
                Run(plan.DuplicateNextCommand);
                break;
            case Key.F5:
                Run(viewModel.Diagnostics.PingSelectedCommand);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private static void Run(ICommand command)
    {
        if (command.CanExecute(null))
        {
            command.Execute(null);
        }
    }

    private void OnDuplicateNext(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } viewModel)
        {
            CommitPlanEdit();
            Run(viewModel.Plan.DuplicateNextCommand);
        }
    }

    private void OnClearMac(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel || !viewModel.Plan.ClearMacCommand.CanExecute(null))
        {
            return;
        }

        int count = viewModel.Plan.Selection.Count(row => !row.Mac.IsEmpty);

        MessageBoxResult answer = MessageBox.Show(
            this,
            $"Take the MAC off {count} row(s)?"
                + Environment.NewLine + Environment.NewLine
                + "Each keeps its planned address and waits for its MAC again, and stops being served until "
                + "it has one. Nothing is sent to any device.",
            "Clear MAC",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        if (answer == MessageBoxResult.Yes)
        {
            viewModel.Plan.ClearMacCommand.Execute(null);
        }
    }

    private void OnSetOnSelection(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel || viewModel.Plan.Selection.Count == 0)
        {
            return;
        }

        CommitPlanEdit();
        DeviceGridViewModel plan = viewModel.Plan;

        new SetOnSelectionWindow(
            plan.Selection.Count,
            (mask, gateway, panel, role) => plan.SetOnSelection(mask, gateway, panel, role))
        {
            Owner = this,
        }.ShowDialog();
    }

    private void OnCopyPlanRows(object sender, RoutedEventArgs e) => CopyPlanRows();

    private void OnPastePlanRows(object sender, RoutedEventArgs e) => PastePlanRows();

    private void OnCopyPlanIp(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.Plan.SelectedRow is { } row && !string.IsNullOrWhiteSpace(row.IpText))
        {
            SetClipboard(row.IpText.Trim());
        }
    }

    private void OnCopyPlanMac(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.Plan.SelectedRow is { } row && !string.IsNullOrWhiteSpace(row.MacText))
        {
            SetClipboard(row.MacText.Trim());
        }
    }

    private void CopyPlanRows()
    {
        if (ViewModel is { } viewModel && viewModel.Plan.Selection.Count > 0)
        {
            SetClipboard(viewModel.Plan.CopyText());
        }
    }

    private void PastePlanRows()
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        string text;

        try
        {
            text = Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty;
        }
        catch (COMException)
        {
            viewModel.Plan.BulkStatus = "Could not read the clipboard - another program is holding it. Try again.";
            return;
        }

        CommitPlanEdit();

        // A pasted row the filter would hide is a row that seems not to have arrived.
        viewModel.Plan.ClearFilterCommand.Execute(null);
        viewModel.Plan.BulkStatus = viewModel.Plan.Paste(text);

        if (viewModel.Plan.SelectedRow is { } first)
        {
            PlanGrid.ScrollIntoView(first);
        }
    }

    private void SetClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (COMException)
        {
            if (ViewModel is { } viewModel)
            {
                viewModel.Plan.BulkStatus = "Could not copy - another program is holding the clipboard. Try again.";
            }
        }
    }

    private void OnOpenPlanRowWebPage(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.Plan.SelectedRow is { } row
            && IPAddress.TryParse(row.IpText.Trim(), out IPAddress? ip)
            && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            Shell.Open(this, $"http://{ip}/");
        }
    }

    private void OnShowHistory(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { Plan.SelectedRow: { } row } viewModel)
        {
            return;
        }

        string name = string.Join("   ", new[] { row.HostName, row.Role, row.IpText, row.MacText }
            .Where(part => !string.IsNullOrWhiteSpace(part)));

        new DeviceHistoryWindow(name, viewModel.Plan.HistoryOf(row)) { Owner = this }.Show();
    }

    /// <summary>
    /// Several rows at once is the one Remove that asks first - a mis-click on a selection of sixty
    /// should not need Undo to recover from. One row is removed straight away, as it always was.
    /// </summary>
    private bool ConfirmRemoveRows(IReadOnlyList<DeviceRowViewModel> rows)
    {
        MessageBoxResult answer = MessageBox.Show(
            this,
            $"Remove {rows.Count} rows from the plan?"
                + Environment.NewLine + Environment.NewLine
                + "Undo remove puts them back. A row the commissioning record already points at is kept.",
            "Remove rows",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        return answer == MessageBoxResult.Yes;
    }

    // ------------------------------------------------------------------ filter

    private void HookPlanFilter(MainViewModel viewModel)
    {
        ICollectionView view = CollectionViewSource.GetDefaultView(viewModel.Plan.Rows);
        view.Filter = item => item is not DeviceRowViewModel row || viewModel.Plan.Matches(row);
    }

    private void OnPlanFilterChanged(object? sender, EventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        // A view cannot be refreshed in the middle of an edit, so the cell being typed is committed first.
        CommitPlanEdit();
        CollectionViewSource.GetDefaultView(viewModel.Plan.Rows).Refresh();
    }

    private void CommitPlanEdit()
    {
        PlanGrid.CommitEdit(DataGridEditingUnit.Cell, exitEditingMode: true);
        PlanGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);
    }

    // ------------------------------------------------------------------ adapter address

    private void OnAdapterSetup(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        if (viewModel.CreateAdapterSetup() is not { } setup)
        {
            MessageBox.Show(
                this,
                "Select the adapter that is plugged into the panel network first, in the Adapter list at the top.",
                "Set adapter address",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        new AdapterSetupWindow(setup) { Owner = this }.ShowDialog();
        viewModel.RefreshAdapters();
    }
}
