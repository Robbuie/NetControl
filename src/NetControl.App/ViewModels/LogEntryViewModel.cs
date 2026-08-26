using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using NetControl.App.Serving;
using NetControl.Core;
using NetControl.Core.Dhcp;

namespace NetControl.App.ViewModels;

/// <summary>
/// One row of the live log.
///
/// <para>Mutable in exactly one respect: <see cref="RepeatCount"/>. A device that has not been
/// answered asks several times a second, and a hundred identical rows bury the one row that
/// explains the problem. Collapsing a retransmit into a counter on the row already there is the
/// difference between a log you read and a log you scroll past.</para>
/// </summary>
public sealed partial class LogEntryViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RepeatText))]
    [NotifyPropertyChangedFor(nameof(IsRepeated))]
    private int _repeatCount = 1;

    [ObservableProperty]
    private DateTimeOffset _lastSeen;

    private LogEntryViewModel(DateTimeOffset timestamp, LogEntryKind kind, string label, string message)
    {
        Timestamp = timestamp;
        _lastSeen = timestamp;
        Kind = kind;
        Label = label;
        Message = message;
    }

    public DateTimeOffset Timestamp { get; }

    public LogEntryKind Kind { get; }

    /// <summary>"DHCP Discover", "BOOTP REPLY", "Fault" - what this row is.</summary>
    public string Label { get; }

    /// <summary>The sentence. Already user-facing; Core writes these, the UI does not rephrase them.</summary>
    public string Message { get; }

    public RequestOutcome Outcome { get; private init; }

    public MacAddress Mac { get; private init; }

    /// <summary>Transaction id, needed to tell one device's retransmit from another device's request.</summary>
    public uint TransactionId { get; private init; }

    /// <summary>Resolved from the packed IEEE table when the row was made, or null.</summary>
    public string? Vendor { get; private init; }

    /// <summary>
    /// What the plan called this device when the row was made, or null for a stranger or an
    /// unlabelled row.
    ///
    /// <para>Snapshotted rather than bound. A log row is an account of a moment; renaming a device
    /// in the grid must not go back and retitle the requests it made before it had that name.</para>
    /// </summary>
    public string? PlannedName { get; private init; }

    public int? ArrivalInterfaceIndex { get; private init; }

    public string? ArrivalAdapter { get; private init; }

    /// <summary>Next action, when there is one. Faults carry these; requests usually do not.</summary>
    public string? Remediation { get; private init; }

    /// <summary>
    /// A MAC that is not in the plan. The log shows these differently, because a stranger on the
    /// network and a device you are waiting for are not the same event.
    /// </summary>
    public bool IsUnknownDevice => Outcome == RequestOutcome.NotInPlan;

    public bool IsRepeated => RepeatCount > 1;

    public string TimeText => Timestamp.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

    public string MacText => Mac.IsEmpty ? string.Empty : Mac.ToString();

    public string VendorText => Vendor ?? (Mac.IsEmpty ? string.Empty : "unknown vendor");

    /// <summary>
    /// The device column. Empty rather than "unknown" for a stranger: the row is already coloured
    /// as one, and inventing a word for the absence of a name would read like a name.
    /// </summary>
    public string NameText => PlannedName ?? string.Empty;

    public string AdapterText => ArrivalInterfaceIndex is { } index
        ? $"[{index}] {ArrivalAdapter ?? "unresolved"}"
        : string.Empty;

    public string RepeatText => RepeatCount > 1
        ? $"x{RepeatCount.ToString(CultureInfo.InvariantCulture)}"
        : string.Empty;

    public string Tooltip => Remediation is null
        ? Message
        : $"{Message}{Environment.NewLine}{Environment.NewLine}{Remediation}";

    /// <summary>
    /// A request as it landed.
    /// </summary>
    /// <param name="planned">
    /// The plan's row for this MAC, or null if it has none. Whether the plan holds the MAC at all
    /// is a different question from whether the policy agreed to serve it, and it is the one that
    /// decides whether this row reads as a stranger.
    /// </param>
    /// <param name="mode">Watch or Serve, so an in-plan device reads honestly in either.</param>
    /// <param name="selectedInterfaceIndex">The adapter the user chose, or null for "any".</param>
    public static LogEntryViewModel FromRequest(
        DhcpRequestEventArgs e,
        string? vendor,
        PlannedDevice? planned,
        DhcpServerMode mode,
        int? selectedInterfaceIndex)
    {
        ArgumentNullException.ThrowIfNull(e);

        RequestOutcome outcome = Classify(e, planned is not null, mode, selectedInterfaceIndex);

        return new LogEntryViewModel(e.Timestamp, LogEntryKind.Request, e.Kind, e.Decision.Reason)
        {
            Outcome = outcome,
            Mac = e.Mac,
            TransactionId = e.Packet.TransactionId,
            Vendor = vendor,
            PlannedName = planned?.DisplayName,
            ArrivalInterfaceIndex = e.ArrivalInterfaceIndex,
            ArrivalAdapter = e.ArrivalNic?.Name,
        };
    }

    /// <summary>A reply that actually reached the wire. This is the row the commissioning record turns on.</summary>
    public static LogEntryViewModel FromReply(DhcpReplyEventArgs e, string? vendor, PlannedDevice? planned)
    {
        ArgumentNullException.ThrowIfNull(e);

        string message = e.Assignment is { } assignment
            ? $"{assignment.Ip} sent to {e.Mac} out [{e.Nic.Index}] {e.Nic.Name} ({e.ByteCount} bytes)"
            : $"refused {e.Mac} out [{e.Nic.Index}] {e.Nic.Name} - the address it asked for is not the planned one";

        return new LogEntryViewModel(e.Timestamp, LogEntryKind.Reply, e.Kind, message)
        {
            Outcome = e.Assignment is null ? RequestOutcome.Refused : RequestOutcome.Served,
            Mac = e.Mac,
            TransactionId = e.Reply.TransactionId,
            Vendor = vendor,
            PlannedName = planned?.DisplayName,
            ArrivalInterfaceIndex = e.Nic.Index,
            ArrivalAdapter = e.Nic.Name,
        };
    }

    /// <summary>
    /// A fault from the engine. Core calls everything it reports a fault; the log splits them,
    /// because only a fatal one means the listener has stopped. An advisory port conflict comes
    /// through here on every start, and showing it in red beside an amber interface bar would
    /// leave the user deciding which of the two to believe.
    /// </summary>
    public static LogEntryViewModel FromFault(DhcpFaultEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        return new LogEntryViewModel(
            e.Timestamp,
            e.IsFatal ? LogEntryKind.Fault : LogEntryKind.Warning,
            e.IsFatal ? "Fault (stopped)" : "Warning",
            e.Message)
        {
            Remediation = e.Remediation,
        };
    }

    /// <summary>Something the tool did: started, stopped, opened a project.</summary>
    public static LogEntryViewModel Notice(DateTimeOffset timestamp, string message, string? remediation = null) =>
        new(timestamp, LogEntryKind.Notice, "Tool", message)
        {
            Remediation = remediation,
        };

    /// <summary>
    /// Whether <paramref name="e"/> is another go at the request this row already represents.
    /// Keyed on transaction id and MAC, which is what makes it the same attempt rather than a new
    /// one from the same device.
    /// </summary>
    public bool Matches(DhcpRequestEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        return Kind == LogEntryKind.Request
            && Mac == e.Mac
            && TransactionId == e.Packet.TransactionId;
    }

    /// <summary>Folds a retransmit into this row.</summary>
    public void NoteRepeat(DateTimeOffset at)
    {
        RepeatCount++;
        LastSeen = at;
    }

    private static RequestOutcome Classify(
        DhcpRequestEventArgs e,
        bool isInPlan,
        DhcpServerMode mode,
        int? selectedInterfaceIndex)
    {
        if (selectedInterfaceIndex is { } selected && selected != e.ArrivalInterfaceIndex)
        {
            return RequestOutcome.WrongAdapter;
        }

        if (e.Decision.ShouldServe)
        {
            // Optimistic by one event: in Serve mode the reply goes out immediately after this,
            // and if the send fails a Fault row lands beside it. The Reply row is the proof.
            return mode == DhcpServerMode.Serve ? RequestOutcome.Served : RequestOutcome.Watching;
        }

        return isInPlan ? RequestOutcome.Refused : RequestOutcome.NotInPlan;
    }
}
