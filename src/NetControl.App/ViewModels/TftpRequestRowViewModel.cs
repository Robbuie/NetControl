using System.Globalization;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using NetControl.Core.Tftp;

namespace NetControl.App.ViewModels;

/// <summary>
/// One line in the TFTP tab's list: a file request as it arrived, or a fault the watch raised.
///
/// <para>The filename is shown exactly as the controller sent it - never trimmed, normalised or
/// case-folded - because it is the one thing in this list a server log will not tell you when it
/// refuses the request. Same rule the codec follows.</para>
/// </summary>
public sealed partial class TftpRequestRowViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RepeatText))]
    private int _repeatCount = 1;

    private TftpRequestRowViewModel(DateTimeOffset timestamp, LogEntryKind kind, string message, string? remediation)
    {
        Timestamp = timestamp;
        Kind = kind;
        Message = message;
        Remediation = remediation;
    }

    public DateTimeOffset Timestamp { get; }

    public LogEntryKind Kind { get; }

    /// <summary>The request's client endpoint - address and port together are its transfer identifier.</summary>
    public IPEndPoint? Source { get; private init; }

    public string SourceText => Source?.ToString() ?? string.Empty;

    public string FileName { get; private init; } = string.Empty;

    /// <summary>WRQ for a backup, RRQ for a restore.</summary>
    public string OperationText { get; private init; } = string.Empty;

    public string ModeText { get; private init; } = string.Empty;

    public string OptionsText { get; private init; } = string.Empty;

    public string AdapterText { get; private init; } = string.Empty;

    /// <summary>What the watch did with it, or the fault itself.</summary>
    public string Message { get; }

    public string? Remediation { get; }

    /// <summary>True when the request carries something that will break the backup - netascii mode, say.</summary>
    public bool HasConcern { get; private init; }

    public string TimeText => Timestamp.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

    public string RepeatText => RepeatCount > 1
        ? "x" + RepeatCount.ToString(CultureInfo.InvariantCulture)
        : string.Empty;

    public string Tooltip => Remediation is null
        ? Message
        : $"{Message}{Environment.NewLine}{Environment.NewLine}{Remediation}";

    public static TftpRequestRowViewModel FromRequest(TftpRequestEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        IReadOnlyList<string> concerns = e.Request.Concerns();
        string message = concerns.Count > 0
            ? $"{e.Reason}. {string.Join(" ", concerns)}"
            : e.Reason;

        LogEntryKind kind = concerns.Count > 0 || e.Action == TftpWatchAction.SendFailed
            ? LogEntryKind.Warning
            : LogEntryKind.Request;

        return new TftpRequestRowViewModel(e.Timestamp, kind, message, remediation: null)
        {
            Source = e.Source,
            FileName = e.Request.FileName,
            OperationText = e.Request.IsWrite ? "WRQ (backup)" : "RRQ (restore)",
            ModeText = e.Request.RawMode,
            OptionsText = e.Request.Options.IsEmpty ? string.Empty : e.Request.Options.ToString(),
            AdapterText = e.ArrivalNic is { } nic
                ? $"[{nic.Index}] {nic.Name}"
                : $"interface {e.ArrivalInterfaceIndex.ToString(CultureInfo.InvariantCulture)}",
            HasConcern = concerns.Count > 0,
        };
    }

    public static TftpRequestRowViewModel FromFault(TftpFaultEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return new TftpRequestRowViewModel(e.Timestamp, LogEntryKind.Fault, e.Message, e.Remediation)
        {
            OperationText = "Fault",
        };
    }

    public static TftpRequestRowViewModel Notice(DateTimeOffset timestamp, string message, string? remediation = null) =>
        new(timestamp, LogEntryKind.Notice, message, remediation) { OperationText = "Note" };

    /// <summary>Whether a retransmit belongs on this row: same client endpoint, same file.</summary>
    public bool IsSameRequestAs(TftpRequestEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return Source is not null
            && Source.Equals(e.Source)
            && string.Equals(FileName, e.Request.FileName, StringComparison.Ordinal);
    }
}
