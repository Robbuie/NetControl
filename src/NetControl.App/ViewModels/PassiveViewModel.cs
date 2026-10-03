using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetControl.App.Composition;
using NetControl.Core;
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
    private bool _isListening;

    [ObservableProperty]
    private bool _promiscuous = true;

    [ObservableProperty]
    private string? _status;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _errorRemediation;

    public PassiveViewModel(
        IUiDispatcher dispatcher,
        Func<ProjectStore> project,
        Func<NicInfo?> adapter,
        ICaptureProvider capture,
        IOuiLookup? oui = null)
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
        if (PassiveFrameParser.TryParse(frame, out PassiveSighting? sighting) && sighting is not null)
        {
            _inventory.Observe(sighting, utc);
            _dirty = true;
        }
    }

    /// <summary>Copies the inventory onto the list - on the UI thread, at most once a second.</summary>
    internal void Refresh()
    {
        if (!_dirty)
        {
            return;
        }

        _dirty = false;
        IReadOnlyList<PassiveDevice> snapshot = _inventory.Snapshot();

        Devices.Clear();
        foreach (PassiveDevice device in snapshot)
        {
            Devices.Add(device);
        }

        if (IsListening && _channel is { } channel)
        {
            int waiting = snapshot.Count(d => d.IsWaitingForAddress);
            Status = $"Listening on {channel.AdapterName}: {snapshot.Count} device(s) in {_inventory.Frames} frame(s)"
                + (waiting > 0 ? $", {waiting} asking for an address and not getting one." : ".");
        }
    }

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
