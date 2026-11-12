// UseWPF drops System.IO from the implicit usings; Export writes a file. See CLAUDE.md.
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using NetControl.App.ViewModels;
using NetControl.Core.Safety;

namespace NetControl.App.Views;

/// <summary>
/// The sent-packet ledger, listen-only and the send cap. Refreshed on a one-second timer from the
/// ledger's snapshot rather than per packet, so a sweep of a thousand addresses costs the UI one
/// redraw a second.
/// </summary>
public partial class NetworkActivityWindow : Window
{
    /// <summary>Newest rows shown. The ledger keeps more, and Export writes all it keeps.</summary>
    private const int MaxRows = 2000;

    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _timer;
    private long _shownSent = -1;
    private long _shownBlocked = -1;
    private int _shownCount = -1;

    public NetworkActivityWindow(MainViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _viewModel = viewModel;

        InitializeComponent();
        DataContext = viewModel;

        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Refresh(force: false), Dispatcher);
        Closed += (_, _) => _timer.Stop();

        Refresh(force: true);
        _timer.Start();
    }

    private SentPacketLedger Ledger => _viewModel.Guard.Ledger;

    private void Refresh(bool force)
    {
        long sent = Ledger.TotalSent;
        long blocked = Ledger.TotalBlocked;
        int count = Ledger.Count;

        if (!force && sent == _shownSent && blocked == _shownBlocked && count == _shownCount)
        {
            return;
        }

        _shownSent = sent;
        _shownBlocked = blocked;
        _shownCount = count;

        string byKind = string.Join(", ", Ledger.Totals().Select(t =>
            string.Create(CultureInfo.CurrentCulture, $"{TransmitBlockedException.Describe(t.Kind)} {t.Sent:N0}{(t.Blocked > 0 ? $" (+{t.Blocked:N0} blocked)" : string.Empty)}")));

        TotalsText.Text = string.Create(CultureInfo.CurrentCulture, $"{sent:N0} sent, {blocked:N0} blocked this session")
            + (byKind.Length > 0 ? ": " + byKind : ".");

        string filter = FilterBox.Text.Trim();
        bool blockedOnly = BlockedOnly.IsChecked == true;

        Packets.ItemsSource = Ledger.Snapshot()
            .Reverse()
            .Where(p => !blockedOnly || p.Blocked)
            .Where(p => filter.Length == 0
                || p.Destination.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || p.Purpose.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || p.Kind.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Take(MaxRows)
            .Select(p => new Row(p))
            .ToList();
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
        {
            Refresh(force: true);
        }
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export sent packets",
            Filter = "CSV (*.csv)|*.csv|All files (*.*)|*.*",
            DefaultExt = ".csv",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = $"NetControl sent packets {DateTime.Now:yyyy-MM-dd HHmm}.csv",
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, Ledger.ToCsv());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            MessageBox.Show(this, $"{dialog.FileName} could not be written: {ex.Message}", "Export", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        Ledger.Clear();
        Refresh(force: true);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>One row of the list.</summary>
    public sealed class Row(SentPacket packet)
    {
        public string When { get; } = packet.Utc.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.CurrentCulture);

        public string Kind { get; } = packet.Kind.ToString();

        public string Destination { get; } = packet.Destination;

        public string Purpose { get; } = packet.Purpose;

        public string Bytes { get; } = packet.Bytes > 0 ? packet.Bytes.ToString(CultureInfo.CurrentCulture) : string.Empty;

        public bool Blocked { get; } = packet.Blocked;

        public string State { get; } = packet.Blocked ? "blocked" : "sent";
    }
}
