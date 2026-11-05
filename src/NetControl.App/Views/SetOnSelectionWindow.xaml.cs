using System.Windows;

namespace NetControl.App.Views;

/// <summary>
/// Asks for the values to set on every selected plan row. The check and the write are
/// <see cref="ViewModels.DeviceGridViewModel.SetOnSelection"/>'s; this window passes what was typed
/// and shows what it says back, staying open on a typo so nothing has to be typed twice.
/// </summary>
public partial class SetOnSelectionWindow : Window
{
    private readonly Func<string?, string?, string?, string?, string?> _apply;

    /// <param name="rowCount">How many rows are selected, for the heading.</param>
    /// <param name="apply">Mask, gateway, panel, role - null for "leave alone" - returning why not, or null when done.</param>
    public SetOnSelectionWindow(int rowCount, Func<string?, string?, string?, string?, string?> apply)
    {
        ArgumentNullException.ThrowIfNull(apply);

        InitializeComponent();
        _apply = apply;
        HeadText.Text = $"Set on {rowCount} selected row(s)";
        Loaded += (_, _) => MaskBox.Focus();
    }

    private void OnSet(object sender, RoutedEventArgs e)
    {
        string? gateway = ClearGatewayBox.IsChecked == true ? string.Empty : Typed(GatewayBox.Text);

        if (_apply(Typed(MaskBox.Text), gateway, Typed(PanelBox.Text), Typed(RoleBox.Text)) is { } problem)
        {
            ErrorText.Text = problem;
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        DialogResult = true;
    }

    private static string? Typed(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
