using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using NetControl.Core.Dhcp;

namespace NetControl.Core.Persistence;

/// <summary>
/// Subscribes to a <see cref="DhcpServer"/> and turns its events into rows in the project's
/// append-only log, plus an <c>Assignment</c> row for every reply that actually went out.
///
/// This is the piece that makes the log a commissioning record rather than a screen that scrolls.
/// It lives here, in Persistence, rather than inside <see cref="DhcpServer"/>, because the engine
/// must not know that a database exists - the same server runs under the CLI and under tests with
/// nothing recording at all.
///
/// <para><b>Two rules govern everything in this class.</b> First, a handler runs on the server's
/// receive loop thread, so it must not throw: a failed INSERT must never be able to stop the tool
/// from answering a device. Failures are counted and surfaced through
/// <see cref="RecordingFailed"/> instead. Second, writing is synchronous and small - roughly two
/// rows per device - which is comfortably inside the gap between a device's retransmits. If that
/// ever stops being true, the fix is a queue in here, not an async signature on the store.</para>
/// </summary>
public sealed class DhcpEventRecorder : IDisposable
{
    private readonly ProjectStore _store;
    private readonly DhcpServer _server;

    /// <summary>MAC to Device row id. Row ids never change, so this only ever needs filling in.</summary>
    private readonly ConcurrentDictionary<MacAddress, long> _deviceIds = new();

    private int _recorded;
    private int _failures;
    private bool _disposed;

    public DhcpEventRecorder(ProjectStore store, DhcpServer server)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(server);

        _store = store;
        _server = server;

        _server.Listening += OnListening;
        _server.RequestReceived += OnRequestReceived;
        _server.ReplySent += OnReplySent;
        _server.Fault += OnFault;
    }

    /// <summary>
    /// Raised when a row could not be written. The server carries on regardless; this exists so
    /// the UI can stop claiming to be recording when it is not. A commissioning record with a
    /// silent hole in it is worse than one that admits it.
    /// </summary>
    public event EventHandler<PersistenceFailedEventArgs>? RecordingFailed;

    /// <summary>
    /// Whether to write a row for a retransmitted request.
    ///
    /// Off by default. A device that has not been answered asks several times a second, and the
    /// reply is sent every time - but a hundred identical rows do not tell the reader anything
    /// the first one did not, and they bury the row that matters. Turn this on when investigating
    /// a device that is asking and not accepting the answer.
    /// </summary>
    public bool RecordRetransmits { get; init; }

    /// <summary>
    /// How many rows failed to write. Zero is the only acceptable value at the end of a job.
    /// Read from a UI thread while the receive loop writes it, hence the interlocked access.
    /// </summary>
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
        _server.ReplySent -= OnReplySent;
        _server.Fault -= OnFault;
    }

    private void OnListening(object? sender, EventArgs e) => Guard("server start", RecordListening);

    private void OnRequestReceived(object? sender, DhcpRequestEventArgs e) =>
        Guard("request", () => RecordRequest(e));

    private void OnReplySent(object? sender, DhcpReplyEventArgs e) => Guard("reply", () => RecordReply(e));

    private void OnFault(object? sender, DhcpFaultEventArgs e) => Guard("fault", () => RecordFault(e));

    // The four Record* methods below are internal rather than inlined into the handlers so the
    // tests can drive them with a hand-built event, without a real socket. The reply path in
    // particular cannot be reached from a hermetic test any other way: making the server actually
    // send one means broadcasting onto whatever network the laptop is plugged into, and a
    // `dotnet test` run must never do that.
    internal bool RecordListening()
    {
        EventDetail detail = new EventDetail()
            .Add("endpoint", _server.LocalEndPoint?.ToString())
            .Add("mode", _server.Options.Mode.ToString())
            .Add("sendMode", _server.Options.SendMode.ToString())
            .Add("interfaceFilter", _server.Options.InterfaceIndexFilter?.ToString(CultureInfo.InvariantCulture)
                ?? "all adapters");

        string where = _server.LocalEndPoint?.ToString() ?? "UDP/67";
        _store.Events.Append(
            EventSeverity.Info,
            EventCategory.Dhcp,
            $"{_server.Options.Mode} mode started on {where}.",
            detail: detail);

        return true;
    }

    internal bool RecordRequest(DhcpRequestEventArgs e)
    {
        if (e.IsRetransmit && !RecordRetransmits)
        {
            return false;
        }

        long? deviceId = FindDeviceId(e.Mac);

        // Severity is about whether something needs fixing, not about whether we replied. An
        // unknown MAC asking is ordinary traffic on a plant network. A device that IS in the plan
        // and still did not get served is a misconfiguration, and that is the line worth finding.
        EventSeverity severity = e.Decision.ShouldServe || deviceId is null
            ? EventSeverity.Info
            : EventSeverity.Warn;

        EventDetail detail = new EventDetail()
            .Add("xid", e.Packet.TransactionId.ToString("X8", CultureInfo.InvariantCulture))
            .Add("secs", e.Packet.Seconds)
            .Add("broadcastFlag", e.Packet.WantsBroadcastReply)
            .Add("interfaceIndex", e.ArrivalInterfaceIndex)
            .Add("destination", e.DestinationAddress.ToString())
            .Add("vendorClass", e.Packet.VendorClass)
            .Add("hostName", e.Packet.HostName)
            .Add("retransmit", e.IsRetransmit)
            .Add("served", e.Decision.ShouldServe)
            .Add("reason", e.Decision.Reason);

        _store.Events.Append(
            severity,
            EventCategory.Dhcp,
            $"{e.Kind} from {e.Mac} on {Describe(e)} - {e.Decision.Reason}.",
            e.Mac.ToString(),
            deviceId,
            detail);

        return true;
    }

    internal bool RecordReply(DhcpReplyEventArgs e)
    {
        long? deviceId = FindDeviceId(e.Mac);

        EventDetail detail = new EventDetail()
            .Add("xid", e.Reply.TransactionId.ToString("X8", CultureInfo.InvariantCulture))
            .Add("yiaddr", e.Reply.YourIp.ToString())
            .Add("subnetMask", e.Assignment?.SubnetMask?.ToString())
            .Add("gateway", e.Assignment?.Gateway?.ToString())
            .Add("interfaceIndex", e.Nic.Index)
            .Add("interfaceName", e.Nic.Name)
            .Add("sendMode", e.SendMode.ToString())
            .Add("destination", e.Destination.ToString())
            .Add("bytes", e.ByteCount);

        _store.Events.Append(
            EventSeverity.Info,
            EventCategory.Dhcp,
            $"{e.Kind} sent to {e.Mac}: {e.Reply.YourIp} via [{e.Nic.Index}] {e.Nic.Name} ({e.ByteCount} bytes).",
            e.Mac.ToString(),
            deviceId,
            detail);

        // The Assignment row is the "what was served" summary. Only a planned device can be
        // served at all, so a missing device id here means the plan changed underneath us; the
        // event row above still records what happened.
        if (e.Assignment is not null && deviceId is { } id)
        {
            _store.Assignments.Record(id, e.Reply.YourIp, e.Nic.Index, e.Nic.Name, e.Timestamp);
        }

        return true;
    }

    internal bool RecordFault(DhcpFaultEventArgs e)
    {
        EventDetail detail = new EventDetail()
            .Add("remediation", e.Remediation)
            .Add("exception", e.Exception?.GetType().Name)
            .Add("exceptionMessage", e.Exception?.Message)
            .Add("fatal", e.IsFatal);

        _store.Events.Append(
            e.IsFatal ? EventSeverity.Error : EventSeverity.Warn,
            EventCategory.Dhcp,
            e.Message,
            detail: detail);

        return true;
    }

    private static string Describe(DhcpRequestEventArgs e) =>
        e.ArrivalNic is { } nic
            ? $"[{nic.Index}] {nic.Name}"
            : $"interface index {e.ArrivalInterfaceIndex.ToString(CultureInfo.InvariantCulture)} "
                + "(adapter no longer present)";

    private long? FindDeviceId(MacAddress mac)
    {
        if (_deviceIds.TryGetValue(mac, out long cached))
        {
            return cached;
        }

        DeviceRecord? device = _store.Devices.Find(mac);
        if (device is null)
        {
            // Not cached: an unknown MAC may well be added to the plan mid-session - that is
            // exactly the drag-from-log-to-grid workflow - and a negative cache would then keep
            // pointing at nothing.
            return null;
        }

        _deviceIds[mac] = device.Id;
        return device.Id;
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "These handlers run on the DHCP receive loop. Nothing that happens while writing a log "
            + "row may be allowed to stop the tool answering a device; the failure is reported instead.")]
    /// <param name="work">Returns false when it deliberately wrote nothing, so the count stays honest.</param>
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
