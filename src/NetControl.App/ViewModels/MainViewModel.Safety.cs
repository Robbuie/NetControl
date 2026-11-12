using System.Globalization;
using NetControl.App.Composition;
using NetControl.App.Diagnostics;
using NetControl.Core.Diagnostics;
using NetControl.Core.Persistence;
using NetControl.Core.Safety;

namespace NetControl.App.ViewModels;

/// <summary>
/// Listen-only, the send cap, the verbose trace and the support bundle - the controls that decide
/// what this tool is allowed to do to the network, and how much it says about what it did.
/// </summary>
public sealed partial class MainViewModel
{
    private readonly Action<SafetySettings>? _saveSafety;

    /// <summary>
    /// True: nothing is transmitted - no scan, ping, CIP, DHCP reply, TFTP refusal or PROFINET frame.
    /// Watching, capturing and every local read keep working.
    /// </summary>
    public bool IsListenOnly
    {
        get => Guard.ListenOnly;
        set
        {
            if (Guard.ListenOnly == value)
            {
                return;
            }

            Guard.ListenOnly = value;
            Record(
                value ? EventSeverity.Warn : EventSeverity.Info,
                value ? "Listen-only mode turned on: nothing will be transmitted." : "Listen-only mode turned off: transmissions allowed.");

            if (value && RunState is Serving.ServerRunState.Listening && Log.Mode == Core.Dhcp.DhcpServerMode.Serve)
            {
                Log.AddNotice(
                    "Listen-only mode is on while serving: requests are still logged, but no replies will be sent.",
                    "Turn Listen only off to answer planned devices again, or switch to Watch.");
            }

            SaveSafety();
        }
    }

    /// <summary>The send cap as text for the settings box; blank or 0 means no cap.</summary>
    public string MaxPacketsPerSecondText
    {
        get => Guard.MaxPacketsPerSecond == 0 ? "0" : Guard.MaxPacketsPerSecond.ToString(CultureInfo.CurrentCulture);
        set
        {
            int parsed = int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out int n) && n >= 0
                ? n
                : NetworkGuard.DefaultMaxPacketsPerSecond;

            if (Guard.MaxPacketsPerSecond != parsed)
            {
                Guard.MaxPacketsPerSecond = parsed;
                SaveSafety();
            }

            OnPropertyChanged();
        }
    }

    /// <summary>The detailed trace - every packet sent and received - in the diagnostic log.</summary>
    public bool IsVerboseLog
    {
        get => Trace is TraceLog { IsVerbose: true };
        set
        {
            if (Trace is not TraceLog log || log.IsVerbose == value)
            {
                return;
            }

            log.IsVerbose = value;
            log.Info(value ? "Verbose logging on." : "Verbose logging off.");
            OnPropertyChanged();
            SaveSafety();
        }
    }

    /// <summary>One line for the status bar: what this tool has put on the wire this session.</summary>
    public string NetworkActivityText => Guard.ListenOnly
        ? string.Create(CultureInfo.CurrentCulture, $"LISTEN ONLY - {Guard.Ledger.TotalBlocked:N0} blocked")
        : string.Create(CultureInfo.CurrentCulture, $"{Guard.Ledger.TotalSent:N0} sent");

    /// <summary>Raises the status-bar line; called by the view on a timer rather than per packet.</summary>
    public void RefreshNetworkActivity() => OnPropertyChanged(nameof(NetworkActivityText));

    /// <summary>
    /// Writes a support bundle. Returns null when written, otherwise why not.
    /// </summary>
    public string? ExportSupportBundle(string path)
    {
        IReadOnlyList<EventRecord>? events = null;
        try
        {
            events = _project.Events.All();
        }
        catch (PersistenceException ex)
        {
            Trace.Warn("Support bundle: the project's event record could not be read.", ex);
        }

        var contents = new SupportBundleContents
        {
            BuildText = BuildInfo.Describe(),
            ProjectName = ProjectName,
            Adapters = _nics.Snapshot(),
            SelectedAdapter = InterfaceBar.SelectedAdapter?.Nic,
            EnvironmentChecks = [.. InterfaceBar.Checks.Select(c => $"{c.Name}: {c.State} - {c.Summary}")],
            ListenerState = $"{RunState} ({Log.Mode})",
            ListenOnly = Guard.ListenOnly,
            MaxPacketsPerSecond = Guard.MaxPacketsPerSecond,
            VerboseLog = IsVerboseLog,
            Findings = Health.LastFindings,
            LedgerCsv = Guard.Ledger.ToCsv(),
            TraceLines = Trace is TraceLog log ? log.Recent() : [],
            ProjectEvents = events,
            LogsFolder = AppPaths.Logs,
            DataFolder = AppPaths.Data,
        };

        string? problem = SupportBundle.Write(path, contents);

        if (problem is null)
        {
            Trace.Info($"Support bundle written to {path}.");
            Log.AddNotice($"Support bundle written to {path}.");
        }
        else
        {
            Trace.Warn(problem);
            SetError(problem, "Choose a folder you can write to - Documents or the desktop.");
        }

        return problem;
    }

    private void OnGuardChanged(object? sender, EventArgs e) => _dispatcher.Post(() =>
    {
        OnPropertyChanged(nameof(IsListenOnly));
        OnPropertyChanged(nameof(MaxPacketsPerSecondText));
        OnPropertyChanged(nameof(NetworkActivityText));
        StartServeCommand.NotifyCanExecuteChanged();
    });

    private void SaveSafety() => _saveSafety?.Invoke(new SafetySettings
    {
        ListenOnly = Guard.ListenOnly,
        MaxPacketsPerSecond = Guard.MaxPacketsPerSecond,
        VerboseLog = IsVerboseLog,
    });

    /// <summary>Distinct MACs that asked in the last ten minutes and are not in the plan.</summary>
    private int UnplannedAskers()
    {
        DateTimeOffset since = _time.GetUtcNow() - TimeSpan.FromMinutes(10);
        return Log.Entries.Where(e => e.IsUnknownDevice && e.Timestamp >= since).Select(e => e.Mac).Distinct().Count();
    }

    private void Record(EventSeverity severity, string message)
    {
        try
        {
            _project.Events.Append(severity, EventCategory.App, message);
        }
        catch (PersistenceException ex)
        {
            SetError($"Could not record that in the project: {ex.Message}", ex.Remediation);
        }
    }
}
