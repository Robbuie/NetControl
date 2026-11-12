using System.Globalization;
using System.Net;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using NetControl.Core.Analysis;

namespace NetControl.App.ViewModels;

/// <summary>
/// One row of the ping monitor. Updated in place each round rather than replaced, so a selected row
/// stays selected and the grid does not rebuild itself every second.
/// </summary>
public sealed partial class PingTargetRowViewModel : ObservableObject
{
    /// <summary>Sparkline size in device-independent pixels; the view draws it at exactly this.</summary>
    public const double SparkWidth = 120;

    public const double SparkHeight = 18;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name))]
    [NotifyPropertyChangedFor(nameof(State))]
    [NotifyPropertyChangedFor(nameof(Last))]
    [NotifyPropertyChangedFor(nameof(Range))]
    [NotifyPropertyChangedFor(nameof(Jitter))]
    [NotifyPropertyChangedFor(nameof(Loss))]
    [NotifyPropertyChangedFor(nameof(OutagesText))]
    [NotifyPropertyChangedFor(nameof(IsDown))]
    [NotifyPropertyChangedFor(nameof(IsDropping))]
    [NotifyPropertyChangedFor(nameof(Sparkline))]
    [NotifyPropertyChangedFor(nameof(Tooltip))]
    private PingTargetStats _stats;

    public PingTargetRowViewModel(PingTargetStats stats) => _stats = stats;

    public IPAddress Address => Stats.Address;

    public string AddressText => Stats.Address.ToString();

    public string Name => Stats.Label ?? string.Empty;

    public string State => Stats.StateText;

    public string Last => Stats.LastText;

    public string Range => Stats.RangeText;

    public string Jitter => Stats.JitterText;

    public string Loss => Stats.LossText;

    public string OutagesText => Stats.OutageText;

    public bool IsDown => Stats.IsDown;

    public bool IsDropping => !Stats.IsDown && Stats.ConsecutiveLost > 0;

    public string Tooltip
    {
        get
        {
            var text = new StringBuilder();
            text.Append(CultureInfo.CurrentCulture, $"{Stats.Sent} sent, {Stats.Received} answered.");

            if (Stats.DownSinceUtc is { } down)
            {
                text.Append(CultureInfo.CurrentCulture, $" Not answering since {down.ToLocalTime():HH:mm:ss}.");
            }

            if (Stats.LastOutageEndUtc is { } end)
            {
                text.Append(CultureInfo.CurrentCulture, $" Last outage ended {end.ToLocalTime():HH:mm:ss}.");
            }

            return text.ToString();
        }
    }

    /// <summary>
    /// The last round trips as polyline points ("x,y x,y ..."), scaled to the slowest one shown. A
    /// lost echo is drawn at the top, so loss reads as a spike.
    /// </summary>
    public string Sparkline
    {
        get
        {
            IReadOnlyList<double?> history = Stats.History;
            if (history.Count < 2)
            {
                return string.Empty;
            }

            double max = Math.Max(1, history.Where(h => h is not null).Select(h => h!.Value).DefaultIfEmpty(1).Max());
            double step = SparkWidth / (history.Count - 1);
            var points = new StringBuilder();

            for (int i = 0; i < history.Count; i++)
            {
                double y = history[i] is { } ms ? SparkHeight - (ms / max * (SparkHeight - 2)) - 1 : 0;
                points.Append(CultureInfo.InvariantCulture, $"{i * step:0.#},{y:0.#} ");
            }

            return points.ToString().TrimEnd();
        }
    }
}
