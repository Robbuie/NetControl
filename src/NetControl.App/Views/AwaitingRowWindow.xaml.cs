using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NetControl.App.ViewModels;

namespace NetControl.App.Views;

/// <summary>
/// Lists the plan rows awaiting their MAC, for a device that has just asked, and returns the one
/// picked. The write is <see cref="MainViewModel.AssignToAwaitingRow"/>'s.
/// </summary>
public partial class AwaitingRowWindow : Window
{
    private readonly IReadOnlyList<AwaitingRowOption> _options;

    public AwaitingRowWindow(string device, IReadOnlyList<AwaitingRowOption> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        InitializeComponent();
        _options = options;

        DeviceText.Text = device;
        RowList.ItemsSource = options;

        Loaded += (_, _) => FilterBox.Focus();
    }

    /// <summary>The row picked. Set when the dialog closes with Assign MAC.</summary>
    public DeviceRowViewModel? Result { get; private set; }

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        List<AwaitingRowOption> shown = [.. _options.Where(option => option.Matches(FilterBox.Text))];
        RowList.ItemsSource = shown;

        if (shown.Count == 1)
        {
            RowList.SelectedIndex = 0;
        }
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e) => Accept();

    private void OnAssign(object sender, RoutedEventArgs e) => Accept();

    private void Accept()
    {
        if (RowList.SelectedItem is not AwaitingRowOption option)
        {
            return;
        }

        Result = option.Row;
        DialogResult = true;
    }
}
