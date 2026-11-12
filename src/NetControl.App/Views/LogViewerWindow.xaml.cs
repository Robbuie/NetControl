using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NetControl.App.Composition;
using NetControl.App.ViewModels;
using NetControl.Core.Diagnostics;

namespace NetControl.App.Views;

/// <summary>
/// The diagnostic log, live. Lines arrive on whatever thread wrote them and are queued; a timer
/// drains the queue onto the screen a few times a second, so a verbose burst costs the UI one
/// update per tick rather than one per line.
/// </summary>
public partial class LogViewerWindow : Window
{
    /// <summary>Lines kept on screen. The file keeps everything; this is a view of the tail.</summary>
    private const int MaxLines = 5000;

    private readonly TraceLog? _log;
    private readonly ConcurrentQueue<TraceLine> _incoming = new();
    private readonly List<TraceLine> _all = [];
    private readonly ObservableCollection<Line> _shown = [];
    private readonly DispatcherTimer _timer;

    public LogViewerWindow(MainViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();
        DataContext = viewModel;
        Lines.ItemsSource = _shown;

        _log = viewModel.Trace as TraceLog;

        if (_log is null)
        {
            WhereText.Text = "This session has no diagnostic log to show.";
        }
        else
        {
            WhereText.Text = _log.IsDisabled
                ? $"The log file could not be written ({_log.LastFailure}); lines are shown here only."
                : $"Written to {_log.FilePath}. Kept for ten files, then the oldest is deleted.";

            _all.AddRange(_log.Recent());
            _log.LineWritten += OnLineWritten;
        }

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) => Drain(), Dispatcher);
        Closed += (_, _) =>
        {
            _timer.Stop();
            if (_log is not null)
            {
                _log.LineWritten -= OnLineWritten;
            }
        };

        Rebuild();
        _timer.Start();
    }

    private void OnLineWritten(object? sender, TraceLine line) => _incoming.Enqueue(line);

    private void Drain()
    {
        if (_incoming.IsEmpty)
        {
            return;
        }

        bool paused = PauseButton.IsChecked == true;
        int minimum = MinimumRank();
        string filter = FilterBox.Text.Trim();
        bool added = false;

        while (_incoming.TryDequeue(out TraceLine? line))
        {
            _all.Add(line);

            if (!paused && Matches(line, minimum, filter))
            {
                _shown.Add(new Line(line));
                added = true;
            }
        }

        Trim();

        if (added && _shown.Count > 0)
        {
            Lines.ScrollIntoView(_shown[^1]);
        }

        UpdateCount();
    }

    private void Rebuild()
    {
        int minimum = MinimumRank();
        string filter = FilterBox.Text.Trim();

        _shown.Clear();
        foreach (TraceLine line in _all.Where(l => Matches(l, minimum, filter)).TakeLast(MaxLines))
        {
            _shown.Add(new Line(line));
        }

        if (_shown.Count > 0)
        {
            Lines.ScrollIntoView(_shown[^1]);
        }

        UpdateCount();
    }

    private void Trim()
    {
        if (_all.Count > MaxLines * 2)
        {
            _all.RemoveRange(0, _all.Count - MaxLines);
        }

        while (_shown.Count > MaxLines)
        {
            _shown.RemoveAt(0);
        }
    }

    private void UpdateCount() =>
        CountText.Text = string.Create(CultureInfo.CurrentCulture, $"{_shown.Count:N0} line(s) shown of {_all.Count:N0} held.");

    private int MinimumRank() =>
        LevelBox?.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out int rank)
            ? rank
            : 1;

    private static bool Matches(TraceLine line, int minimum, string filter) =>
        line.Rank >= minimum
        && (filter.Length == 0 || line.Message.Contains(filter, StringComparison.OrdinalIgnoreCase));

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
        {
            Rebuild();
        }
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, _shown.Select(l => l.Text)));
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Another program is holding the clipboard. Press Copy again.
        }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e) => Shell.Open(this, AppPaths.Logs);

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>One line as shown.</summary>
    public sealed class Line(TraceLine line)
    {
        public string Text { get; } = string.Create(
            CultureInfo.CurrentCulture,
            $"{line.Utc.ToLocalTime():HH:mm:ss.fff}  {line.Level,-5}  {line.Message}");

        public string? Detail { get; } = line.Detail;

        public int Rank { get; } = line.Rank;
    }
}
