using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using NetControl.App.Diagnostics;

namespace NetControl.App.Views;

/// <summary>
/// <see cref="ReadinessState"/> to a brush. The mapping lives here rather than in four XAML
/// triggers so the interface bar and the log cannot drift apart on what amber means.
/// </summary>
[ValueConversion(typeof(ReadinessState), typeof(Brush))]
public sealed class ReadinessBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Ready = Frozen(0x1B, 0x7F, 0x3B);
    private static readonly SolidColorBrush Unknown = Frozen(0x6B, 0x6B, 0x6B);
    private static readonly SolidColorBrush Warning = Frozen(0xB2, 0x6A, 0x00);
    private static readonly SolidColorBrush Blocked = Frozen(0xB0, 0x00, 0x20);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        ReadinessState.Ready => Ready,
        ReadinessState.Warning => Warning,
        ReadinessState.Blocked => Blocked,

        // Unknown, and anything unexpected. Never green: a check we could not run is not a check
        // that passed.
        _ => Unknown,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("Readiness is displayed, never edited.");

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
