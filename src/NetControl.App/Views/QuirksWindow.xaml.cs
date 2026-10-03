using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using NetControl.Core;

namespace NetControl.App.Views;

/// <summary>
/// Ticks for one device's quirks. No view model of its own: the catalog in Core says what each one
/// is, and the grid records the change - what is left is a list of checkboxes and a Save button.
/// </summary>
public partial class QuirksWindow : Window
{
    private readonly List<Choice> _choices;

    public QuirksWindow(string deviceName, DeviceQuirks current)
    {
        InitializeComponent();

        DeviceLine.Text = deviceName;
        _choices = [.. DeviceQuirkCatalog.All.Select(info => new Choice(info) { IsChecked = current.HasFlag(info.Flag) })];
        Choices.ItemsSource = _choices;

        // Bits no build of this tool has a name for are kept, never silently dropped by a Save.
        Unnamed = current & ~_choices.Aggregate(DeviceQuirks.None, (all, c) => all | c.Info.Flag);
    }

    /// <summary>What was ticked when Save was pressed.</summary>
    public DeviceQuirks Result { get; private set; }

    private DeviceQuirks Unnamed { get; }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        Result = _choices.Where(c => c.IsChecked).Aggregate(Unnamed, (all, c) => all | c.Info.Flag);
        DialogResult = true;
    }

    /// <summary>One row: the catalog entry and whether it is ticked.</summary>
    public sealed partial class Choice(DeviceQuirkInfo info) : ObservableObject
    {
        public DeviceQuirkInfo Info { get; } = info;

        [ObservableProperty]
        private bool _isChecked;
    }
}
