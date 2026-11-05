using System.Globalization;
using System.Windows;
using NetControl.Core.Persistence;

namespace NetControl.App.Views;

/// <summary>Shows one device's events from the project record. Modeless, read only.</summary>
public partial class DeviceHistoryWindow : Window
{
    private readonly IReadOnlyList<Line> _lines;

    public DeviceHistoryWindow(string device, IReadOnlyList<EventRecord> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        InitializeComponent();

        HeadText.Text = device;
        _lines = [.. events.Select(e => new Line(
            e.Utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture),
            e.Severity.ToString(),
            e.Message))];

        EventList.ItemsSource = _lines;
        EmptyText.Visibility = _lines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        string text = string.Join(
            Environment.NewLine,
            [HeadText.Text, .. _lines.Select(line => $"{line.When}\t{line.Severity}\t{line.Message}")]);

        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Another program is holding the clipboard. Nothing is lost; press Copy again.
        }
    }

    /// <summary>One row of the list.</summary>
    public sealed record Line(string When, string Severity, string Message);
}
