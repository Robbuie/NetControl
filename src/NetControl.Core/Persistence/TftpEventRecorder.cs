using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using NetControl.Core.Tftp;

namespace NetControl.Core.Persistence;

/// <summary>
/// Subscribes to a <see cref="TftpWatchServer"/> and turns what it sees into rows in the project's
/// append-only log.
///
/// <para>The sibling of <see cref="DhcpEventRecorder"/>, and it lives here for the same reason: the
/// watch must not know that a database exists, so the same server can run under a spike, under a
/// test, and under the app with nothing recording at all.</para>
///
/// <para><b>The same two rules govern this class.</b> A handler runs on the watch's receive loop, so
/// it must not throw - a failed INSERT must never stop the tool recording the next request, which
/// on a bench session may be the only one anybody gets. Failures are counted and surfaced through
/// <see cref="RecordingFailed"/>. And writing is synchronous and small: one row per request, which
/// is comfortably inside the gap between a controller's retransmits.</para>
///
/// <para><b>No row here carries a device id.</b> TFTP has no hardware address in it, so the only
/// identity available is the source IP - and turning that into a planned device would mean guessing
/// at a join this tool has no evidence for. The address goes in <see cref="EventRecord.Target"/>
/// and the device column stays null, which is the same rule the rest of this product follows: a log
/// row carries what was observed and nothing that was inferred.</para>
/// </summary>
public sealed class TftpEventRecorder : IDisposable
{
    private readonly ProjectStore _store;
    private readonly TftpWatchServer _server;

    private int _recorded;
    private int _failures;
    private bool _disposed;

    public TftpEventRecorder(ProjectStore store, TftpWatchServer server)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(server);

        _store = store;
        _server = server;

        _server.Listening += OnListening;
        _server.RequestReceived += OnRequestReceived;
        _server.Fault += OnFault;
    }

    /// <summary>
    /// Raised when a row could not be written. The watch carries on regardless; this exists so the
    /// UI can stop claiming to be recording when it is not.
    /// </summary>
    public event EventHandler<PersistenceFailedEventArgs>? RecordingFailed;

    /// <summary>
    /// Whether to write a row for a retransmitted request.
    ///
    /// <para>Off by default, as on the DHCP side. A controller that is not getting anywhere asks
    /// again on every timeout, and identical rows bury the first one - which is the row that
    /// carries the filename. Turn it on when the question is specifically about how a controller
    /// retries, which is one of the open questions the bench session is meant to answer.</para>
    /// </summary>
    public bool RecordRetransmits { get; init; }

    /// <summary>How many rows failed to write. Zero is the only acceptable value at the end of a job.</summary>
    public int FailureCount => Volatile.Read(ref _failures);

    /// <summary>How many rows this recorder has written.</summary>
    public int RecordedCount => Volatile.Read(ref _recorded);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _server.Listening -= OnListening;
        _server.RequestReceived -= OnRequestReceived;
        _server.Fault -= OnFault;
    }

    private void OnListening(object? sender, EventArgs e) => Guard("watch start", RecordListening);

    private void OnRequestReceived(object? sender, TftpRequestEventArgs e) =>
        Guard("request", () => RecordRequest(e));

    private void OnFault(object? sender, TftpFaultEventArgs e) => Guard("fault", () => RecordFault(e));

    // Internal rather than inlined into the handlers so the tests can drive them with a hand-built
    // event and no socket at all - the same reason DhcpEventRecorder does it.
    internal bool RecordListening()
    {
        EventDetail detail = new EventDetail()
            .Add("endpoint", _server.LocalEndPoint?.ToString())
            .Add("mode", _server.Options.Mode.ToString())
            .Add("sendRefusal", _server.Options.SendRefusal)
            .Add("refusalSource", _server.Options.RefusalSource.ToString())
            .Add("interfaceFilter", _server.Options.InterfaceIndexFilter?.ToString(CultureInfo.InvariantCulture)
                ?? "all adapters");

        string where = _server.LocalEndPoint?.ToString()
            ?? $"UDP/{_server.Options.ListenPort.ToString(CultureInfo.InvariantCulture)}";

        _store.Events.Append(
            EventSeverity.Info,
            EventCategory.Tftp,
            $"TFTP {_server.Options.Mode} mode started on {where}.",
            detail: detail);

        return true;
    }

    internal bool RecordRequest(TftpRequestEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (e.IsRetransmit && !RecordRetransmits)
        {
            return false;
        }

        IReadOnlyList<string> concerns = e.Request.Concerns();

        // Severity is about whether something needs fixing, not about whether we answered. In
        // Observe mode every request is refused by design, so refusing is not a warning - a
        // request that would corrupt the image, or one we could not answer at all, is.
        EventSeverity severity = concerns.Count > 0 || e.Action == TftpWatchAction.SendFailed
            ? EventSeverity.Warn
            : EventSeverity.Info;

        EventDetail detail = new EventDetail()
            .Add("operation", e.Request.IsWrite ? "WRQ" : "RRQ")
            .Add("fileName", e.Request.FileName)
            .Add("mode", e.Request.RawMode)
            .Add("sourcePort", e.Source.Port)
            .Add("interfaceIndex", e.ArrivalInterfaceIndex)
            .Add("interfaceName", e.ArrivalNic?.Name)
            .Add("destination", e.ArrivalAddress.ToString())
            .Add("retransmit", e.IsRetransmit)
            .Add("action", e.Action.ToString())
            .Add("reason", e.Reason);

        // Which port the refusal left from, when one was sent. Whether a controller accepts a
        // refusal from the well-known port or only one carrying a transfer identifier of its own is
        // an open question, and a row that does not say which was tried cannot answer it later.
        if (e.RefusalSourcePort is int refusedFrom)
        {
            detail.Add("refusalFrom", refusedFrom);
        }

        // The options are the half of a request no server log records, and the gap between what
        // was asked for and what a server grants is where "it works on my bench" lives.
        if (!e.Request.Options.IsEmpty)
        {
            detail.Add("options", e.Request.Options.ToString());
        }

        // Only when it is needed. A filename that will not print is exactly the case where the
        // decoded string is not evidence and the bytes are, and it is also rare enough that
        // carrying hex on every row would be noise.
        if (!IsPrintable(e.Request.FileName))
        {
            detail.AddHex("fileNameBytes", e.Request.FileNameBytes);
        }

        foreach (string concern in concerns)
        {
            detail.Add("concern", concern);
        }

        _store.Events.Append(
            severity,
            EventCategory.Tftp,
            $"{e.Describe()} - {e.Reason}.",
            e.Source.Address.ToString(),
            detail: detail);

        return true;
    }

    internal bool RecordFault(TftpFaultEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        EventDetail detail = new EventDetail()
            .Add("remediation", e.Remediation)
            .Add("exception", e.Exception?.GetType().Name)
            .Add("exceptionMessage", e.Exception?.Message)
            .Add("fatal", e.IsFatal);

        _store.Events.Append(
            e.IsFatal ? EventSeverity.Error : EventSeverity.Warn,
            EventCategory.Tftp,
            e.Message,
            detail: detail);

        return true;
    }

    /// <summary>
    /// Whether a decoded filename is worth showing as text on its own. Anything outside printable
    /// ASCII means the Latin-1 decoding kept bytes a reader cannot check against a capture.
    /// </summary>
    private static bool IsPrintable(string value)
    {
        foreach (char c in value)
        {
            if (c is < ' ' or > '~')
            {
                return false;
            }
        }

        return true;
    }

    /// <param name="work">Returns false when it deliberately wrote nothing, so the count stays honest.</param>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "These handlers run on the TFTP receive loop. Nothing that happens while writing a log "
            + "row may stop the tool recording the next request; the failure is reported instead.")]
    private void Guard(string what, Func<bool> work)
    {
        try
        {
            if (work())
            {
                Interlocked.Increment(ref _recorded);
            }
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _failures);
            RecordingFailed?.Invoke(this, new PersistenceFailedEventArgs(what, ex));
        }
    }
}
