using System.Windows;
using System.Windows.Controls;
using NetControl.App.ViewModels;
using NetControl.Core.Plan;

namespace NetControl.App.Views;

/// <summary>
/// Picks one sheet of a project IP workbook and the mask and gateway per subnet. Imports nothing
/// itself: on Import it hands back the choice, and <see cref="MainViewModel.ImportWorkbookSheet"/>
/// does the rest.
/// </summary>
public partial class WorkbookImportWindow : Window
{
    /// <summary>
    /// Kept across sheet changes, so a mask typed for 192.168.1 while looking at SF101 is still there
    /// after a look at SF102.
    /// </summary>
    private readonly Dictionary<string, WorkbookSubnetRow> _subnets = new(StringComparer.Ordinal);

    public WorkbookImportWindow(string path, PlanWorkbook workbook)
    {
        ArgumentNullException.ThrowIfNull(workbook);

        InitializeComponent();

        FileText.Text = System.IO.Path.GetFileName(path);
        SheetBox.ItemsSource = workbook.Sheets;
        SheetBox.SelectedIndex = 0;
    }

    /// <summary>The sheet to import. Set when the dialog closes with Import.</summary>
    public PlanSheet? Sheet { get; private set; }

    /// <summary>Mask and gateway per subnet of <see cref="Sheet"/>, keyed as <see cref="PlanSheet.Subnets"/>.</summary>
    public IReadOnlyDictionary<string, PlanSubnetSettings> Subnets { get; private set; } =
        new Dictionary<string, PlanSubnetSettings>();

    private void OnSheetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SheetBox.SelectedItem is not PlanSheet sheet)
        {
            return;
        }

        SummaryText.Text = Describe(sheet);
        SubnetList.ItemsSource = sheet.Subnets.Select(Row).ToList();
    }

    private void OnImport(object sender, RoutedEventArgs e)
    {
        if (SheetBox.SelectedItem is not PlanSheet sheet)
        {
            return;
        }

        Sheet = sheet;
        Subnets = sheet.Subnets.ToDictionary(
            prefix => prefix,
            prefix => new PlanSubnetSettings(Row(prefix).Mask, Row(prefix).Gateway),
            StringComparer.Ordinal);

        DialogResult = true;
    }

    private WorkbookSubnetRow Row(string prefix)
    {
        if (!_subnets.TryGetValue(prefix, out WorkbookSubnetRow? row))
        {
            row = new WorkbookSubnetRow(prefix);
            _subnets[prefix] = row;
        }

        return row;
    }

    private static string Describe(PlanSheet sheet)
    {
        string devices = sheet.DeviceCount == 1 ? "1 device" : $"{sheet.DeviceCount} devices";
        string ports = sheet.SecondPortCount == 0
            ? string.Empty
            : $", {sheet.SecondPortCount} of them second ports (IP2)";
        string skipped = sheet.SkippedWithoutAddress == 0
            ? string.Empty
            : $" {sheet.SkippedWithoutAddress} named row(s) with no address are skipped (spares and the like).";
        string merged = sheet.MergedRepeats == 0
            ? string.Empty
            : $" {sheet.MergedRepeats} repeated row(s) - the same name at the same address - are merged.";

        string temporary = sheet.TemporaryAddresses == 0
            ? string.Empty
            : $" {sheet.TemporaryAddresses} planned on their Temp address (the Final one is in Notes; re-import after cutover).";
        string samePort = sheet.SamePortAddresses == 0
            ? string.Empty
            : $" {sheet.SamePortAddresses} same-port IP2 address(es) are kept in Notes - one MAC gets one address.";

        return $"{devices}{ports}.{temporary}{samePort}{skipped}{merged}";
    }
}
