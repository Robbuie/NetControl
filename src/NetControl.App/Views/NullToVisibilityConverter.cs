using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace NetControl.App.Views;

/// <summary>
/// Shows an element only when the bound value is present. Used for the error banner and the
/// misdirected-traffic note, both of which are absent far more often than not.
/// </summary>
[ValueConversion(typeof(object), typeof(Visibility))]
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null || (value is string text && string.IsNullOrWhiteSpace(text))
            ? Visibility.Collapsed
            : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("One way only.");
}
