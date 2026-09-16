using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using NetControl.App.Appearance;
using NetControl.App.Diagnostics;

namespace NetControl.App.Views;

/// <summary>
/// <see cref="ReadinessState"/> to a brush.
///
/// <para>The mapping itself lives in <see cref="ReadinessBrushes"/>, which owns four brushes that
/// are repainted whenever the theme changes. This class is the thin adapter that lets XAML reach
/// them - it deliberately does not create a brush of its own, because a converter is asked once
/// per binding and a brush it made would still be showing the theme that was running when the
/// window opened. See the remarks on <see cref="ReadinessBrushes"/>.</para>
/// </summary>
[ValueConversion(typeof(ReadinessState), typeof(Brush))]
public sealed class ReadinessBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        ReadinessBrushes.For(value is ReadinessState state ? state : ReadinessState.Unknown);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("Readiness is displayed, never edited.");
}
