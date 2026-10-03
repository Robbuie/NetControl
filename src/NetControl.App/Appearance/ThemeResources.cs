using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace NetControl.App.Appearance;

/// <summary>
/// Turns a resolved token set into WPF resources and puts them on the application.
///
/// <para>This is the half of the design system that is toolkit-specific. Qt substitutes
/// <c>var(--bg-1)</c> into a text stylesheet once per theme change; WPF cannot, so the tokens
/// become a <see cref="ResourceDictionary"/> and the styles in <c>Controls.xaml</c> reference them
/// with <c>{DynamicResource bg-1}</c>. Swapping the dictionary re-resolves every one of those
/// references without touching a single style - which is what makes the live preview in the
/// appearance dialog possible.</para>
///
/// <para><b>Every colour token is published twice</b>, as a <see cref="SolidColorBrush"/> under its
/// own name and as a <see cref="Color"/> under <c>name.color</c>. A <c>GradientStop</c> takes a
/// Color and a <c>Background</c> takes a Brush, and a style that needs the one it was not given has
/// no way to convert - so both are always there and neither is ever missing for only some tokens.
/// </para>
/// </summary>
public static class ThemeResources
{
    /// <summary>
    /// Token names that are not colours. Everything not listed here becomes a brush and a colour,
    /// so a new colour token needs no change to this file and a new metric needs exactly one line.
    /// </summary>
    private static readonly HashSet<string> NonColour = new(StringComparer.Ordinal)
    {
        "ui-font", "tb-h", "row-h", "status-h", "side-w", "tbtn-h", "icon", "field-h",
        "small-font", "grid-row-h", "radius", "radius-sm", "radius-lg", "font", "mono", "is-light",
    };

    /// <summary>
    /// The resources for one token set: a frozen brush and a colour per colour token, a
    /// <see cref="double"/> per metric, two <see cref="FontFamily"/> and two
    /// <see cref="CornerRadius"/>.
    /// </summary>
    public static ThemeDictionary Build(IReadOnlyDictionary<string, string> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        var dictionary = new ThemeDictionary();

        foreach (KeyValuePair<string, string> pair in tokens)
        {
            if (NonColour.Contains(pair.Key))
            {
                continue;
            }

            Color colour = Theme.ColorOf(pair.Value);

            // Frozen because these are read from every template in the app and never edited in
            // place: a theme change replaces the whole dictionary rather than repainting a brush.
            var brush = new SolidColorBrush(colour);
            brush.Freeze();

            dictionary[pair.Key] = brush;
            dictionary[pair.Key + ".color"] = colour;
        }

        foreach (string metric in NonColour)
        {
            if (metric is "font" or "mono" or "is-light" || !tokens.TryGetValue(metric, out string? raw))
            {
                continue;
            }

            dictionary[metric] = double.Parse(raw, CultureInfo.InvariantCulture);
        }

        // A comma-separated list is a FontFamily's own fallback syntax, so "Segoe UI, Inter"
        // means what it says - the second is used on a machine without the first.
        dictionary["font"] = new FontFamily(tokens["font"]);
        dictionary["mono"] = new FontFamily(tokens["mono"]);

        // Border.CornerRadius cannot take a double, and writing the number into forty templates
        // is forty places the radius stops being a token.
        dictionary["corner"] = new CornerRadius((double)dictionary["radius"]!);
        dictionary["corner-sm"] = new CornerRadius((double)dictionary["radius-sm"]!);
        dictionary["corner-lg"] = new CornerRadius((double)dictionary["radius-lg"]!);

        // THE TWO GRADIENTS ARE BUILT HERE RATHER THAN IN XAML, AND THEY HAVE TO BE.
        // A {DynamicResource} inside a Freezable that sits in a Setter.Value - which is what a
        // GradientStop in a style is - does not resolve: the brush is shared between every
        // element using the style and has no place in the element tree to look a resource up
        // from. The symptom is a primary button that comes out transparent, and only in a build
        // where the style was reached through a Setter rather than inlined. Composing them here
        // means the styles reference one finished brush by name, like every other colour.
        dictionary["accent-fill"] = Gradient(tokens["accent-hi"], tokens["accent"]);
        dictionary["chrome-fill"] = Gradient(tokens["bg-2"], tokens["bg-1"]);

        return dictionary;
    }

    /// <summary>Top-to-bottom, the only direction either of this app's gradients runs.</summary>
    private static LinearGradientBrush Gradient(string top, string bottom)
    {
        var brush = new LinearGradientBrush(
            Theme.ColorOf(top), Theme.ColorOf(bottom), new Point(0, 0), new Point(0, 1));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Replaces the live token dictionary, or adds one if this is the first call.
    ///
    /// <para>It goes at the front of <see cref="ResourceDictionary.MergedDictionaries"/> so that
    /// the control styles - which are merged by <c>App.xaml</c> and never replaced - sit above it
    /// and can be found by name either way round. Replacing the entry rather than clearing and
    /// re-adding it is deliberate: WPF invalidates every <c>{DynamicResource}</c> that resolved
    /// through the old dictionary in one pass, so the whole window repaints on the theme it was
    /// given instead of flashing through a state with no resources at all.</para>
    /// </summary>
    public static void Install(Application app, IReadOnlyDictionary<string, string> tokens)
    {
        ArgumentNullException.ThrowIfNull(app);

        ThemeDictionary built = Build(tokens);
        var merged = app.Resources.MergedDictionaries;

        for (int i = 0; i < merged.Count; i++)
        {
            if (merged[i] is ThemeDictionary)
            {
                merged[i] = built;
                return;
            }
        }

        merged.Insert(0, built);
    }
}
