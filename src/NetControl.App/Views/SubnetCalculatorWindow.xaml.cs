using System.Windows;
using NetControl.App.ViewModels;

namespace NetControl.App.Views;

/// <summary>
/// The subnet calculator. Everything it decides is in <see cref="SubnetCalculatorViewModel"/> and
/// <see cref="NetControl.Core.SubnetCalculation"/>, where the tests are; this is the window.
/// </summary>
public partial class SubnetCalculatorWindow : Window
{
    public SubnetCalculatorWindow(string? initial = null)
    {
        InitializeComponent();
        DataContext = new SubnetCalculatorViewModel(initial);

        // The address box first, with what is in it selected, so typing replaces the adapter's own
        // subnet rather than appending to it.
        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
