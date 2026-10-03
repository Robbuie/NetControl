using System.Windows;
using System.Windows.Controls;
using NetControl.App.Appearance;

namespace NetControl.App.Views;

/// <summary>
/// Picks one combination of theme, accent and density, and applies it live.
///
/// <para>No view model, deliberately. Every decision this window makes is already in
/// <see cref="Theme"/> - which is where the tests are - and what is left is three combo boxes and
/// an OK button. A view model here would be a layer whose only job is to forward.</para>
///
/// <para><b>Cancel is not a no-op.</b> Choosing is previewing, so by the time somebody presses
/// Cancel the fourth theme they tried is on screen; putting back what was running when the dialog
/// opened is the whole of what Cancel means.</para>
/// </summary>
public partial class AppearanceWindow : Window
{
    private readonly AppearanceChoice _start;

    /// <summary>True while the combo boxes are being filled, so seeding them previews nothing.</summary>
    private bool _loading;

    public AppearanceWindow()
    {
        InitializeComponent();

        _start = Theme.CurrentChoice;

        _loading = true;
        ThemeBox.ItemsSource = Theme.Themes.Values;
        AccentBox.ItemsSource = Theme.Accents.Values;
        DensityBox.ItemsSource = Theme.Densities.Values;
        LightBox.ItemsSource = Theme.Themes.Values.Where(t => Theme.LightThemes.Contains(t.Id)).ToList();
        DarkBox.ItemsSource = Theme.Themes.Values.Where(t => !Theme.LightThemes.Contains(t.Id)).ToList();

        ThemeBox.SelectedValue = _start.ThemeName;
        AccentBox.SelectedValue = _start.Accent;
        DensityBox.SelectedValue = _start.Density;
        LightBox.SelectedValue = _start.LightTheme;
        DarkBox.SelectedValue = _start.DarkTheme;
        FollowBox.IsChecked = _start.Follow == Theme.FollowWindows;
        FollowPanel.IsEnabled = FollowBox.IsChecked == true;
        _loading = false;

        RefreshNotes();
    }

    /// <summary>What the three controls currently say. Normalised, so it is always renderable.</summary>
    private AppearanceChoice Choice => Theme.Normalise(new AppearanceChoice(
        ThemeBox.SelectedValue as string,
        AccentBox.SelectedValue as string,
        DensityBox.SelectedValue as string,
        FollowBox.IsChecked == true ? Theme.FollowWindows : Theme.FollowOff,
        LightBox.SelectedValue as string,
        DarkBox.SelectedValue as string));

    private void OnChoiceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        RefreshNotes();
        Theme.Apply(Application.Current, Choice);
    }

    private void OnFollowChanged(object sender, RoutedEventArgs e)
    {
        FollowPanel.IsEnabled = FollowBox.IsChecked == true;

        if (!_loading)
        {
            RefreshNotes();
            Theme.Apply(Application.Current, Choice);
        }
    }

    private void RefreshNotes()
    {
        AppearanceChoice choice = Choice;
        ThemeNote.Text = Theme.Themes[choice.ThemeName!].Note;
        DensityNote.Text = Theme.Densities[choice.Density!].Note;
    }

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        // Saving is the only thing OK does that choosing did not already do - the theme has been
        // on screen since the moment it was picked.
        AppearanceStore.Save(Choice);
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Theme.Apply(Application.Current, _start);
        DialogResult = false;
    }
}
