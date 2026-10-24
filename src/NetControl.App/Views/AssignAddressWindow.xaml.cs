using System.Windows;
using NetControl.App.ViewModels;
using NetControl.Core.Commissioning;

namespace NetControl.App.Views;

/// <summary>
/// The Assign IP box. Parsing and every rule live in <see cref="AddressAssignment.TryCreate"/>;
/// this fills the boxes, shows the problems, and hands back a valid assignment.
/// </summary>
public partial class AssignAddressWindow : Window
{
    private readonly AssignDefaults _defaults;

    public AssignAddressWindow(AssignDefaults defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);

        InitializeComponent();
        _defaults = defaults;

        DeviceText.Text = $"{defaults.Mac}   {defaults.Vendor}";
        AdapterText.Text = defaults.AdapterAddress.Length == 0
            ? $"Served from {defaults.AdapterName}."
            : $"Served from {defaults.AdapterName}, this PC at {defaults.AdapterAddress}.";

        IpBox.Text = defaults.Ip;
        MaskBox.Text = defaults.Mask;
        GatewayBox.Text = defaults.Gateway;

        Loaded += (_, _) =>
        {
            IpBox.Focus();
            IpBox.SelectAll();
        };
    }

    /// <summary>The validated assignment, once Assign has succeeded.</summary>
    public AddressAssignment? Result { get; private set; }

    private void OnAssign(object sender, RoutedEventArgs e)
    {
        if (!AddressAssignment.TryCreate(
                IpBox.Text,
                MaskBox.Text,
                GatewayBox.Text,
                MakeStaticBox.IsChecked == true,
                _defaults.AdapterSubnets,
                out AddressAssignment? assignment,
                out IReadOnlyList<string> problems))
        {
            ProblemText.Text = string.Join(Environment.NewLine, problems);
            ProblemPanel.Visibility = Visibility.Visible;
            return;
        }

        Result = assignment;
        DialogResult = true;
    }
}
