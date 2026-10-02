using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using NetControl.App.Diagnostics;
using NetControl.App.Serving;
using NetControl.Core.Dhcp;
using NetControl.Core.Interfaces;
using NetControl.Core.Oui;

namespace NetControl.App.ViewModels;

/// <summary>
/// The always-visible bar: which adapter, what state it is in, who owns UDP/67, what the firewall
/// will do, and whether the listener is actually running.
///
/// <para>This is the single biggest usability win over the tool it replaces. "Nothing is
/// arriving" has four common causes - the wrong adapter, a port conflict, the firewall, and a
/// device that simply is not asking - and three of them are measurable before a packet lands. The
/// bar rules those three out so the user never has to guess which one they are looking at.</para>
///
/// <para>It grades rather than summarises. A wildcard bind by VMware and the Hyper-V Default
/// Switch holding its own address are both "port in use", and reporting them the same way is
/// exactly what trains people to ignore the warning.</para>
/// </summary>
public sealed partial class InterfaceBarViewModel : ObservableObject
{
    /// <summary>
    /// How old the packed IEEE table may get before the bar mentions it. Six months is roughly
    /// when enough new blocks have been issued for "unknown vendor" to start being the table's
    /// fault rather than the device's.
    /// </summary>
    private static readonly TimeSpan OuiStaleAfter = TimeSpan.FromDays(180);

    private readonly Dictionary<int, AdapterOption> _options = [];
    private readonly TimeProvider _time;
    private readonly int _listenPort;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanArmServeMode))]
    private AdapterOption? _selectedAdapter;

    [ObservableProperty]
    private ReadinessCheck _adapterCheck;

    [ObservableProperty]
    private ReadinessCheck _portConflictCheck;

    [ObservableProperty]
    private ReadinessCheck _firewallRuleCheck;

    [ObservableProperty]
    private ReadinessCheck _listenerCheck;

    [ObservableProperty]
    private IReadOnlyList<ReadinessCheck> _checks = [];

    [ObservableProperty]
    private ReadinessState _overall = ReadinessState.Unknown;

    [ObservableProperty]
    private string _headline = "Pick the adapter that is patched into the panel.";

    /// <summary>
    /// Set when a request has arrived on an adapter other than the selected one. Not folded into
    /// the readiness grade, because it is not a precondition - it is the answer, arriving late,
    /// to the question the user is already asking.
    /// </summary>
    [ObservableProperty]
    private string? _misdirectedTraffic;

    [ObservableProperty]
    private string _ouiSummary = string.Empty;

    [ObservableProperty]
    private bool _ouiIsStale;

    public InterfaceBarViewModel(OuiDatabase oui, TimeProvider? timeProvider = null, int listenPort = 67)
    {
        ArgumentNullException.ThrowIfNull(oui);

        _time = timeProvider ?? TimeProvider.System;
        _listenPort = listenPort;

        _adapterCheck = ReadinessCheck.NotChecked(
            "Adapter",
            "No adapter selected.",
            "Pick the adapter that is patched into the panel. Nothing is chosen for you: on a plant "
                + "network, guessing which port to serve from is not a decision a tool should make.");

        _portConflictCheck = ReadinessCheck.NotChecked($"UDP/{listenPort}", "Not checked yet.");
        _firewallRuleCheck = ReadinessCheck.NotChecked("Firewall", "Not checked yet.");
        _listenerCheck = ReadinessCheck.NotChecked("Listener", "Not started.", "Press Start to begin listening.");

        DescribeOui(oui);
        Recompute();
    }

    /// <summary>Every adapter seen this session, best candidates first. Nothing is selected by default.</summary>
    public ObservableCollection<AdapterOption> Adapters { get; } = [];

    /// <summary>
    /// Whether Serve mode may be armed. A reply is a limited broadcast but the socket still needs
    /// a real address on the segment to send it from, so an adapter that is down, addressless or
    /// on APIPA can watch but can never answer.
    /// </summary>
    public bool CanArmServeMode => SelectedAdapter?.CanServe == true;

    /// <summary>
    /// Rebuilds the picker from a fresh inventory, keeping the user's selection through an
    /// adapter disappearing and coming back. Call on the UI thread.
    /// </summary>
    public void ApplyInventory(IReadOnlyList<NicInfo> nics)
    {
        ArgumentNullException.ThrowIfNull(nics);

        // Group rather than ToDictionary, for the same reason NicMonitor does: a duplicate index
        // should be impossible, and a crash in the interface bar would be a much worse outcome
        // than quietly preferring the first.
        Dictionary<int, NicInfo> byIndex = nics
            .GroupBy(n => n.Index)
            .ToDictionary(g => g.Key, g => g.First());

        foreach (NicInfo nic in nics)
        {
            if (_options.TryGetValue(nic.Index, out AdapterOption? existing))
            {
                existing.Update(nic);
                continue;
            }

            var option = new AdapterOption(nic);
            _options[nic.Index] = option;
            Adapters.Add(option);
        }

        foreach (AdapterOption option in _options.Values.ToList())
        {
            if (byIndex.TryGetValue(option.Index, out NicInfo? live))
            {
                option.Update(live);
                continue;
            }

            option.Update(null);

            // An adapter that has gone away and was not chosen is just noise in the list. One the
            // user chose stays, greyed, because clearing their selection behind their back is how
            // a tool ends up listening to nothing and saying nothing about it.
            if (!ReferenceEquals(option, SelectedAdapter))
            {
                _options.Remove(option.Index);
                Adapters.Remove(option);
            }
        }

        SortAdapters();
        Recompute();
    }

    /// <summary>Applies the port and firewall findings. Call on the UI thread.</summary>
    public void ApplyPreflight(PreflightResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        PortConflictCheck = GradePort(result.Port);
        FirewallRuleCheck = GradeFirewall(result.Firewall);
        Recompute();
    }

    /// <summary>Applies the listener's state. Call on the UI thread.</summary>
    public void ApplyListener(ServerRunState state, DhcpServerMode mode, string? stopReason)
    {
        ListenerCheck = GradeListener(state, mode, stopReason);
        Recompute();
    }

    /// <summary>
    /// Records that a request landed on an adapter other than the selected one. Idempotent for
    /// the same adapter, so a device retransmitting does not rewrite the line every second.
    /// </summary>
    public void NoteArrival(int arrivalInterfaceIndex, string arrivalName)
    {
        if (SelectedAdapter is null || SelectedAdapter.Index == arrivalInterfaceIndex)
        {
            MisdirectedTraffic = null;
            return;
        }

        MisdirectedTraffic =
            $"A device is asking on [{arrivalInterfaceIndex}] {arrivalName}, which is not the adapter you "
                + $"selected ({SelectedAdapter.DisplayName}). Either select that adapter or move the patch lead.";
    }

    /// <summary>Clears the misdirected-traffic note, e.g. after the user changes adapter.</summary>
    public void ClearArrivalNote() => MisdirectedTraffic = null;

    partial void OnSelectedAdapterChanged(AdapterOption? value)
    {
        MisdirectedTraffic = null;
        Recompute();
    }

    private void DescribeOui(OuiDatabase oui)
    {
        DateTime source = oui.SourceDateUtc;
        string count = oui.Count.ToString("N0", CultureInfo.InvariantCulture);

        OuiIsStale = _time.GetUtcNow().UtcDateTime - source > OuiStaleAfter;
        OuiSummary = OuiIsStale
            ? $"IEEE OUI table: {count} assignments, packed {source:yyyy-MM-dd}. Stale - a screenful of "
                + "unknown vendors is probably this, not the devices. Refresh it with the OuiPacker tool."
            : $"IEEE OUI table: {count} assignments, packed {source:yyyy-MM-dd}.";
    }

    private void SortAdapters()
    {
        List<AdapterOption> desired = [.. Adapters.OrderBy(Rank).ThenBy(a => a.Index)];

        for (int target = 0; target < desired.Count; target++)
        {
            int current = Adapters.IndexOf(desired[target]);
            if (current != target)
            {
                // Move rather than Clear/Add: a ComboBox loses its selection when its source is
                // rebuilt, and losing the selected adapter because a VPN came up would be exactly
                // the silent failure this tool exists to remove.
                Adapters.Move(current, target);
            }
        }
    }

    /// <summary>
    /// Ordering only. An engineering laptop shows a dozen adapters of which one is real, and
    /// putting the plausible ones at the top is worth doing - but the heuristic never decides
    /// anything, it only sorts.
    /// </summary>
    private static int Rank(AdapterOption option) => option switch
    {
        { IsPresent: false } => 3,
        { IsLikelyVirtual: true } => 2,
        { CanServe: false } => 1,
        _ => 0,
    };

    private ReadinessCheck GradeAdapter()
    {
        if (SelectedAdapter is not { } option)
        {
            return ReadinessCheck.NotChecked(
                "Adapter",
                "No adapter selected.",
                "Pick the adapter that is patched into the panel.");
        }

        if (option.Nic is not { } nic)
        {
            return new ReadinessCheck(
                "Adapter",
                ReadinessState.Blocked,
                $"{option.DisplayName} is no longer present - it has been unplugged or disabled.",
                "Plug it back in. The selection is kept, so it will start working again on its own.");
        }

        if (!nic.IsUp)
        {
            return new ReadinessCheck(
                "Adapter",
                ReadinessState.Blocked,
                $"{option.DisplayName} reports link '{nic.Status}'. Nothing can arrive on a port with no link.",
                "Check the patch lead and the switch port.");
        }

        if (nic.IPv4 is null)
        {
            return new ReadinessCheck(
                "Adapter",
                ReadinessState.Blocked,
                $"{option.DisplayName} has no IPv4 address, so there is no address to send a reply from.",
                "Give it a static address on the panel's subnet.");
        }

        if (nic.IsApipa)
        {
            return new ReadinessCheck(
                "Adapter",
                ReadinessState.Warning,
                $"{option.DisplayName} only holds an APIPA address ({nic.IPv4}) - it asked for a lease and "
                    + "never got one. Requests will still arrive and be logged, but no reply can be sent "
                    + "from 169.254.x.x.",
                "Give it a static address on the panel's subnet before arming Serve mode.");
        }

        return new ReadinessCheck(
            "Adapter",
            ReadinessState.Ready,
            $"{option.DisplayName} is up on {option.AddressText}.");
    }

    private static ReadinessCheck GradePort(PortConflictReport report)
    {
        ReadinessState state = report.Severity switch
        {
            PortConflictSeverity.None => ReadinessState.Ready,
            PortConflictSeverity.Advisory => ReadinessState.Warning,
            _ => ReadinessState.Blocked,
        };

        return new ReadinessCheck($"UDP/{report.Port}", state, report.Summary, report.Remediation);
    }

    /// <summary>Internal so the TFTP tab grades its UDP/69 rule with exactly the same rules.</summary>
    internal static ReadinessCheck GradeFirewall(FirewallStatus status)
    {
        ReadinessState state = status.Verdict switch
        {
            FirewallVerdict.Allowed or FirewallVerdict.NotEnforced => ReadinessState.Ready,
            FirewallVerdict.NoRule => ReadinessState.Warning,
            FirewallVerdict.Blocked => ReadinessState.Blocked,

            // Unknown is not good news and must not be shown as green. See ReadinessState.
            _ => ReadinessState.Unknown,
        };

        return new ReadinessCheck("Firewall", state, status.Summary, status.Remediation);
    }

    private ReadinessCheck GradeListener(ServerRunState state, DhcpServerMode mode, string? stopReason)
    {
        string port = _listenPort.ToString(CultureInfo.InvariantCulture);

        return state switch
        {
            ServerRunState.Listening when mode == DhcpServerMode.Serve => new ReadinessCheck(
                "Listener",
                ReadinessState.Ready,
                $"Serving planned devices on UDP/{port}. Only MACs in the plan will ever be answered.",
                null),

            ServerRunState.Listening => new ReadinessCheck(
                "Listener",
                ReadinessState.Ready,
                $"Listening on UDP/{port}. Watch mode: nothing is transmitted.",
                null),

            ServerRunState.Starting => ReadinessCheck.NotChecked("Listener", "Binding the socket..."),
            ServerRunState.Stopping => ReadinessCheck.NotChecked("Listener", "Stopping..."),

            ServerRunState.Faulted => new ReadinessCheck(
                "Listener",
                ReadinessState.Blocked,
                stopReason ?? "The listener stopped because of a fault.",
                "Start it again. If it recurs, check whether the adapter was removed or reset."),

            _ => ReadinessCheck.NotChecked("Listener", "Not started.", "Press Start to begin listening."),
        };
    }

    private void Recompute()
    {
        AdapterCheck = GradeAdapter();

        // The selected adapter can lose its address without the selection itself changing - a
        // cable out, a VPN taking the interface down - so this is re-raised here rather than left
        // to the generated notification on SelectedAdapter, which would never fire for that case.
        OnPropertyChanged(nameof(CanArmServeMode));

        IReadOnlyList<ReadinessCheck> all = [AdapterCheck, PortConflictCheck, FirewallRuleCheck, ListenerCheck];
        Checks = all;

        ReadinessState worst = ReadinessCheck.Worst(all);
        Overall = worst;

        if (worst == ReadinessState.Ready)
        {
            Headline = SelectedAdapter is { } option
                ? $"Ready - a request on {option.DisplayName} will be received."
                : "Ready.";
            return;
        }

        // The worst thing is the thing worth saying. Anything else and the sentence that explains
        // the silence is buried under three that do not.
        ReadinessCheck lead = all.First(c => c.State == worst);
        Headline = lead.Summary;
    }
}
