using System.Windows;

namespace NetControl.App.Appearance;

/// <summary>
/// A marker so the live token dictionary can be found again and replaced.
///
/// <para>Without it, <see cref="ThemeResources.Install"/> would have to remember an index into
/// <see cref="ResourceDictionary.MergedDictionaries"/>, and any future <c>Merge</c> anywhere in the
/// app would silently shift it - the failure being a theme switch that replaces the control styles
/// with a table of brushes and leaves a window with no templates at all.</para>
/// </summary>
public sealed class ThemeDictionary : ResourceDictionary
{
}
