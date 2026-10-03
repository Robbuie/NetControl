using System.Windows;

namespace NetControl.App.Appearance;

/// <summary>
/// One attached property for the shared title bar: something to put in it beside the title. The
/// main window puts its menu there, File Manager-style, so the row that drags the window is also
/// the row the menus live in; a dialog puts nothing there and gets a plain title.
/// </summary>
public static class Chrome
{
    public static readonly DependencyProperty TitleContentProperty = DependencyProperty.RegisterAttached(
        "TitleContent", typeof(object), typeof(Chrome), new FrameworkPropertyMetadata(null));

    public static object? GetTitleContent(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(TitleContentProperty);
    }

    public static void SetTitleContent(DependencyObject element, object? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(TitleContentProperty, value);
    }
}
