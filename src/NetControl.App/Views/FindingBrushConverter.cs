using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using NetControl.App.Appearance;
using NetControl.Core.Analysis;

namespace NetControl.App.Views;

/// <summary>
/// A finding's severity as the interface bar's own four brushes - so a problem on the Network health
/// tab is the same red as a problem on the bar, in every theme.
/// </summary>
[ValueConversion(typeof(FindingSeverity), typeof(Brush))]
public sealed class FindingBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        FindingSeverity.Error => ReadinessBrushes.Blocked,
        FindingSeverity.Warn => ReadinessBrushes.Warning,
        FindingSeverity.Ok => ReadinessBrushes.Ready,
        _ => ReadinessBrushes.Unknown,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("A finding is displayed, never edited.");
}
