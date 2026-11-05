using System.Windows;
using NetControl.App.ViewModels;

namespace NetControl.App.Views;

/// <summary>
/// The laptop adapter's address. Everything is <see cref="AdapterSetupViewModel"/>'s; the window
/// only supplies the confirmation, which is a dialog.
/// </summary>
public partial class AdapterSetupWindow : Window
{
    public AdapterSetupWindow(AdapterSetupViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();
        DataContext = viewModel;
        viewModel.Confirm = question => MessageBox.Show(
            this,
            question,
            "Change adapter",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No) == MessageBoxResult.Yes;
    }
}
