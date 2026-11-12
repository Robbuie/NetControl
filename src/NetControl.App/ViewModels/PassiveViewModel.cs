using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetControl.App.Composition;
using NetControl.Core;
using NetControl.Core.Analysis;
using NetControl.Core.Capture;
using NetControl.Core.Interfaces;
using NetControl.Core.Oui;
using NetControl.Core.Passive;
using NetControl.Core.Persistence;

namespace NetControl.App.ViewModels;

/// <summary>
/// The Passive tab: listen on one adapter and list every device heard - by ARP, DHCP, LLDP,
/// PROFINET and EtherNet/IP traffic - <b>without sending a single frame.</b>
///
/// <para>That is the difference from a scan, and the reason it exists: some sites do not allow a
/// scan at all, some devices answer nothing, and a device that is asking for an address over and
/// over is visible here whether or not anything answers it. The capture channel this opens is only
/// ever read from; <see cref="PassiveFrameParser"/> has no way to send.</para>
///
/// <para>Needs Npcap. Without it the tab says so, says where to get it, and the rest of NetControl
/// is untouched.</para>
/// </summary>
public sealed partial class PassiveViewModel : ObservableObject
{
    private readonly IUiDispatcher _dispatcher;
    private readonly Func<ProjectStore> _project;
    private readonly Func<NicInfo?> _adapter;
    private readonly ICaptureProvider _capture;
    private readonly PassiveInventory _inventory;
    private readonly AddressConflictDetector? _conflicts;

    private IFrameChannel? _channel;
    private CancellationTokenSource? _refresh;
    private volatile bool _dirty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAvailable))]
    [NotifyPropertyChangedFor(nameof(AvailabilityText))]
    [NotifyPropertyChangedFor(nameof(Remediation))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private CaptureAvailability? _availability;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyPropertyChangedFor(nameof(LiveTraffic))]
    private bool _isListening;

    [ObservableProperty]
    private bool _promiscuous = true;

    [ObservableProperty]
    private string? _status;

    [ObservableProperty]
    private TrafficSnapshot _traffic = TrafficSnapshot.Empty;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _errorRemediation;

    public PassiveViewModel(
        IUiDispatcher dispatcher,
        Func<ProjectStore> project,
        Func<NicInfo?> adapter,
        ICaptureProvider capture,
        IOuiLookup? oui = null,
        AddressConflictDetector? conflicts = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(capture);

        _dispatcher = dispatcher;
        _project = project;
        _adapter = adapter;
        _capture = capture;
        _inventory = new PassiveInventory(oui);
        _conflicts = conflicts;
        TrafficMonitor = new TrafficMonitor(oui: oui);
    }

    /// <summary>Counts what is heard and says what it means. See the traffic panel on the Passive tab.</summary>
    public TrafficMonitor TrafficMonitor { get; }

    /// <summary>The traffic monitor's latest view while listening, otherwise null - for Troubleshoot.</summary>
    public TrafficSnapshot? LiveTraffic => IsListening ? Traffic : null;

    /// <summary>The worst traffic finding's title, for the one-line summary above the device list.</summary>
    public string TrafficHeadline => Traffic.Findings.FirstOrDefault() is { } first
        ? $"{first.SeverityText}: {first.Title}"
        : "Traffic: nothing heard yet.";

    public string TrafficRates => Traffic.Seconds < 1
        ? string.Empty
        : string.Create(
            System.Globalization.CultureInfo.CurrentCulture,
            $"{Traffic.BroadcastPerSecond:0} broadcast/s, {Traffic.MulticastPerSecond:0} multicast/s, {Traffic.ArpPerSecond:0} ARP/s, peak {Traffic.PeakBroadcastPerSecond:N0} broadcast in one second, {Traffic.TopologyChanges} spanning-tree change(s), {Traffic.OtherDhcpServers.Count} other DHCP server(s).");

    partial void OnTrafficChanged(TrafficSnapshot value)
    {
        OnPropertyChanged(nameof(TrafficHeadline));
        OnPropertyChanged(nameof(TrafficRates));
        OnPropertyChanged(nameof(LiveTraffic));
    }

    public ObservableCollection<PassiveDevice> Devices { get; } = [];

    public bool IsAvailable => Availability?.IsAvailable == true;

    public string AvailabilityText => Availability?.Headline ?? "Not checked yet.";

    public string? Remediation => Availability?.Remediation;

    public static string DownloadUrl => CaptureAvailability.DownloadUrl;

    /// <summary>Looks for Npcap. Cheap; run when the tab is first shown and on "Check again".</summary>
    [RelayCommand]
    public void CheckAvailability() => Availability = _capture.Check();

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        ClearError();

        if (_adapter() is not { } nic)
        {
            SetError("No adapter is selected.", "Listening happens on exactly one adapter, chosen at the top of the window.");
            return;
        }

        try
        {
            _channel = _capture.Open(nic, PassiveFrameParser.Filter, Promiscuous);
        }
        catch (CaptureException ex)
        {
            SetError(ex.Message, ex.Remediation);
            return;
        }

        _channel.FrameReceived += OnFrame;
        _channel.Faulted += OnFaulted;
        IsListening = true;
        _inventory.Clear();
        TrafficMonitor.Clear();
        TrafficMonitor.SetOwnMacs([nic.Mac]);
        Traffic = TrafficSnapshot.Empty;
        Devices.Clear();

        Record(EventSeverity.Info, $"Passive listening started on {_channel.AdapterName}. Nothing is transmitted.");
        Status = $"Listening on {_channel.AdapterName}. Nothing is sent - devices appear as they talk.";

        _refresh = new CancellationTokenSource();

        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(_refresh.Token).ConfigureAwait(true))
            {
                Refresh();
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        if (_channel is not { } channel)
        {
            return;
        }

        _refresh?.Cancel();
        _refresh?.Dispose();
        _refresh = null;

        channel.FrameReceived -= OnFrame;
        channel.Faulted -= OnFaulted;
        channel.Dispose();
        _channel = null;
        IsListening = false;

        Refresh();
        Record(
            EventSeverity.Info,
            $"Passive listening on {channel.AdapterName} stopped: {_inventory.Count} device(s) heard in {_inventory.Frames} frame(s).");
        Status = $"Stopped. {_inventory.Count} device(s) heard in {_inventory.Frames} frame(s).";
    }

    /// <summary>Folds one frame in. Called on the capture thread; does no UI work.</summary>
    internal void Observe(byte[] frame, DateTimeOffset utc)
    {
        // Every frame is counted, parsed or not: a storm of something this tool cannot name is still a storm.
        TrafficMonitor.Observe(frame, utc);
        _conflicts?.ObserveFrame(frame, utc);

        if (PassiveFrameParser.TryParse(frame, out PassiveSighting? sighting) && sighting is not null)
        {
            _inventory.Observe(sighting, utc);
            _dirty = true;
        }
    }

    /// <summary>Copies the inventory onto the list - on the UI thread, at most once a second.</summary>
    internal void Refresh()
    {
        // The traffic view moves every second even when no new device has spoken.
        if (IsListening)
        {
            Traffic = TrafficMonitor.Snapshot();
        }

        if (!_dirty)
        {
            return;
        }

        _dirty = false;
        IReadOnlyList<PassiveDevice> snapshot = _inventory.Snapshot();

        // In place: a row is replaced only where it changed, and the list is never cleared - so on a
        // busy segment the grid keeps its scroll position and does not rebuild every second.
        for (int i = 0; i < snapshot.Count; i++)
        {
            if (i < Devices.Count)
            {
                if (!SameRow(Devices[i], snapshot[i]))
                {
                    Devices[i] = snapshot[i];
                }
            }
            else
            {
                Devices.Add(snapshot[i]);
            }
        }

        while (Devices.Count > snapshot.Count)
        {
            Devices.RemoveAt(Devices.Count - 1);
        }

        if (IsListening && _channel is { } channel)
        {
            int waiting = snapshot.Count(d => d.IsWaitingForAddress);
            Status = $"Listening on {channel.AdapterName}: {snapshot.Count} device(s) in {_inventory.Frames} frame(s)"
                + (waiting > 0 ? $", {waiting} asking for an address and not getting one." : ".")
                + (_inventory.Overflow > 0 ? $" {_inventory.Overflow} sighting(s) of further MACs not kept - more than {PassiveInventory.MaxDevices} devices." : string.Empty);
        }
    }

    private static bool SameRow(PassiveDevice shown, PassiveDevice now) =>
        shown.Mac == now.Mac && shown.LastHeardUtc == now.LastHeardUtc && shown.Frames == now.Frames;

    private bool CanStart() => !IsListening && IsAvailable;

    private bool CanStop() => IsListening;

    private void OnFrame(object? sender, FrameEventArgs e) => Observe(e.Frame, e.Utc);

    private void OnFaulted(object? sender, string message) => _dispatcher.Post(() =>
    {
        SetError(message, "Start listening again; if it keeps stopping, check the adapter and cable.");
        Stop();
    });

    private void Record(EventSeverity severity, string message)
    {
        try
        {
            _project().Events.Append(severity, EventCategory.App, message);
        }
        catch (PersistenceException ex)
        {
            SetError($"Could not record that in the project: {ex.Message}", ex.Remediation);
        }
    }

    private void SetError(string message, string? remediation)
    {
        ErrorMessage = message;
        ErrorRemediation = remediation;
    }

    private void ClearError()
    {
        ErrorMessage = null;
        ErrorRemediation = null;
    }
}
