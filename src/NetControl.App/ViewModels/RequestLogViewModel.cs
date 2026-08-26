using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using NetControl.App.Serving;
using NetControl.Core.Dhcp;
using NetControl.Core.Oui;

namespace NetControl.App.ViewModels;

/// <summary>
/// The live request log: every BOOTP/DHCP request as it lands, with the adapter it arrived on,
/// the vendor behind the MAC, and whether it matched a planned device.
///
/// <para>Everything here runs on the UI thread. Core raises its events on the receive loop, so
/// the caller marshals - see <see cref="MainViewModel"/>. Keeping the marshalling out of this
/// class is what lets a test drive it with real event args and no dispatcher.</para>
/// </summary>
public sealed partial class RequestLogViewModel : ObservableObject
{
    /// <summary>
    /// How far back to look for the row a retransmit belongs to. A retransmit arrives within a
    /// few seconds of its original, so anything deeper than a handful of rows would be a
    /// different attempt - and two devices interleaving needs only a couple of rows of slack.
    /// </summary>
    private const int RetransmitScanDepth = 8;

    /// <summary>
    /// How far back to look for a row's place when it arrives out of timestamp order. See
    /// <see cref="InsertionIndex"/> - the skew is milliseconds, so this only has to be big enough
    /// to cover a burst of requests landing while one queued notice is still in flight.
    /// </summary>
    private const int OutOfOrderWindow = 16;

    private readonly List<LogEntryViewModel> _all = [];
    private readonly IOuiLookup _oui;
    private readonly IPlanIndex _plan;
    private readonly TimeProvider _time;

    [ObservableProperty]
    private bool _showOnlyUnknownDevices;

    [ObservableProperty]
    private int _requestCount;

    [ObservableProperty]
    private int _servedCount;

    [ObservableProperty]
    private int _unknownDeviceCount;

    [ObservableProperty]
    private int _faultCount;

    public RequestLogViewModel(IOuiLookup oui, IPlanIndex plan, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(oui);
        ArgumentNullException.ThrowIfNull(plan);

        _oui = oui;
        _plan = plan;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>What the view binds to: newest first, filtered.</summary>
    public ObservableCollection<LogEntryViewModel> Entries { get; } = [];

    /// <summary>
    /// Rows kept before the oldest are dropped. A commissioning session runs to thousands of
    /// rows; the SQLite event log is the record, this is a window onto it.
    /// </summary>
    public int Capacity { get; init; } = 2000;

    /// <summary>
    /// Whether the server is transmitting. Only affects how an in-plan request reads, so that a
    /// Watch-mode row never claims an address was handed out.
    /// </summary>
    public DhcpServerMode Mode { get; set; } = DhcpServerMode.Watch;

    /// <summary>
    /// The adapter the user chose, or null for "any". Used to mark a request that arrived
    /// somewhere else - the single most useful thing this log can say.
    /// </summary>
    public int? SelectedInterfaceIndex { get; set; }

    public void OnRequest(DhcpRequestEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (e.IsRetransmit && TryFoldRetransmit(e))
        {
            return;
        }

        PlannedDevice? planned = _plan.Find(e.Mac);

        LogEntryViewModel entry = LogEntryViewModel.FromRequest(
            e,
            _oui.Lookup(e.Mac),
            planned,
            Mode,
            SelectedInterfaceIndex);

        RequestCount++;

        if (entry.Outcome == RequestOutcome.NotInPlan)
        {
            UnknownDeviceCount++;
        }

        Add(entry);
    }

    public void OnReply(DhcpReplyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        LogEntryViewModel entry = LogEntryViewModel.FromReply(e, _oui.Lookup(e.Mac), _plan.Find(e.Mac));

        if (entry.Outcome == RequestOutcome.Served)
        {
            ServedCount++;
        }

        Add(entry);
    }

    public void OnFault(DhcpFaultEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        FaultCount++;
        Add(LogEntryViewModel.FromFault(e));
    }

    /// <summary>Something the tool did, rather than something a device did.</summary>
    public void AddNotice(string message, string? remediation = null) =>
        Add(LogEntryViewModel.Notice(_time.GetUtcNow(), message, remediation));

    public void Clear()
    {
        _all.Clear();
        Entries.Clear();
        RequestCount = 0;
        ServedCount = 0;
        UnknownDeviceCount = 0;
        FaultCount = 0;
    }

    partial void OnShowOnlyUnknownDevicesChanged(bool value) => RebuildView();

    private bool TryFoldRetransmit(DhcpRequestEventArgs e)
    {
        int depth = Math.Min(RetransmitScanDepth, _all.Count);

        for (int i = 0; i < depth; i++)
        {
            LogEntryViewModel candidate = _all[i];
            if (!candidate.Matches(e))
            {
                continue;
            }

            candidate.NoteRepeat(e.Timestamp);
            return true;
        }

        // A retransmit whose original has already scrolled past. Better a fresh row than a
        // silently dropped one: the log has to account for every request that arrived.
        return false;
    }

    private void Add(LogEntryViewModel entry)
    {
        int at = InsertionIndex(entry.Timestamp);
        _all.Insert(at, entry);

        if (_all.Count > Capacity)
        {
            LogEntryViewModel dropped = _all[^1];
            _all.RemoveAt(_all.Count - 1);
            Entries.Remove(dropped);

            // A row old enough to sort to the very bottom of a full log is a row that arrived
            // already past the horizon. It has been dropped, so it must not then appear in the
            // view - the two collections have to agree about what exists.
            if (ReferenceEquals(dropped, entry))
            {
                return;
            }
        }

        if (!Passes(entry))
        {
            return;
        }

        // Entries is _all with the filter applied and the order kept, so the position in the view
        // is however many rows above this one survived the filter.
        int viewAt = 0;
        for (int i = 0; i < at; i++)
        {
            if (Passes(_all[i]))
            {
                viewAt++;
            }
        }

        Entries.Insert(viewAt, entry);
    }

    /// <summary>
    /// Where a row belongs, newest first.
    ///
    /// <para>Not simply position zero, because rows do not arrive in timestamp order. Core stamps
    /// an event on the receive loop and it reaches the UI thread through a queued dispatcher post,
    /// while a notice the app raises about itself is added inline - so a "Watching" line can
    /// overtake a fault stamped a few milliseconds earlier and sit above it. Two rows in the wrong
    /// order is a small thing on screen and a serious one in a commissioning record, and a log
    /// whose timestamps do not descend is a log people stop trusting.</para>
    ///
    /// <para>The scan is bounded: the skew being corrected for is milliseconds, so anything older
    /// than the window is old enough that its exact slot does not matter, and an append stays
    /// cheap no matter how long the session has run.</para>
    /// </summary>
    private int InsertionIndex(DateTimeOffset timestamp)
    {
        int depth = Math.Min(OutOfOrderWindow, _all.Count);

        for (int i = 0; i < depth; i++)
        {
            if (_all[i].Timestamp <= timestamp)
            {
                return i;
            }
        }

        return depth;
    }

    private bool Passes(LogEntryViewModel entry) =>
        !ShowOnlyUnknownDevices || entry.IsUnknownDevice;

    private void RebuildView()
    {
        Entries.Clear();

        foreach (LogEntryViewModel entry in _all)
        {
            if (Passes(entry))
            {
                Entries.Add(entry);
            }
        }
    }
}
