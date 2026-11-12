using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetControl.App.AdapterSetup;
using NetControl.Core.AdapterSetup;
using NetControl.Core.Interfaces;
using NetControl.Core.Persistence;
using NetControl.Core.Safety;

namespace NetControl.App.ViewModels;

/// <summary>
/// Puts an address on the laptop's own adapter: from a profile saved in the project, from one of the
/// plan's subnets, or typed - and puts the adapter back the way it was afterwards.
///
/// <para><b>Shown, checked, confirmed, read back.</b> The netsh lines are on screen as the fields are
/// typed. The check refuses an address a device in the plan is meant to have, a gateway off the
/// subnet, an address another adapter holds. The confirmation names the adapter and the lines. And
/// the change is only called done when Windows reports the adapter holding it - netsh saying it
/// worked is not the same thing, the same way a CIP success is not a Verified device.</para>
///
/// <para><b>Restore.</b> Before the first change to an adapter its settings are saved to this laptop,
/// and they stay saved through further changes until Restore puts them back. So Restore always means
/// "what it had before NetControl touched it", which is the office network nearly every time.</para>
/// </summary>
public sealed partial class AdapterSetupViewModel : ObservableObject
{
    private readonly IAdapterConfigurator _configurator;
    private readonly IAdapterSnapshotStore _snapshots;
    private readonly Func<IReadOnlyList<NicInfo>> _adapters;
    private readonly ProjectStore? _project;
    private readonly Func<bool> _isListening;
    private readonly Action _refreshAdapters;
    private readonly TimeProvider _time;
    private bool _loadingProfile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAddressMode))]
    private AdapterIpMode _mode = AdapterIpMode.Replace;

    [ObservableProperty]
    private string _ipText = string.Empty;

    [ObservableProperty]
    private string _maskText = "255.255.255.0";

    [ObservableProperty]
    private string _gatewayText = string.Empty;

    [ObservableProperty]
    private string _dnsText = string.Empty;

    /// <summary>The laptop's host number on each plan subnet, as text; kept in the project.</summary>
    [ObservableProperty]
    private string _hostNumberText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteProfileCommand))]
    private AdapterProfile? _selectedProfile;

    /// <summary>What to call the fields when saved as a profile.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveProfileCommand))]
    private string _profileName = string.Empty;

    [ObservableProperty]
    private string _currentText = string.Empty;

    [ObservableProperty]
    private string? _savedText;

    [ObservableProperty]
    private string _commandsText = string.Empty;

    [ObservableProperty]
    private string? _problemsText;

    [ObservableProperty]
    private string? _warningsText;

    [ObservableProperty]
    private string? _status;

    [ObservableProperty]
    private bool _statusIsBad;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private bool _canApplyChange;

    public AdapterSetupViewModel(
        NicInfo adapter,
        IAdapterConfigurator configurator,
        IAdapterSnapshotStore snapshots,
        Func<IReadOnlyList<NicInfo>> adapters,
        ProjectStore? project,
        Func<bool> isListening,
        Action? refreshAdapters = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(configurator);
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(adapters);
        ArgumentNullException.ThrowIfNull(isListening);

        Adapter = adapter;
        _configurator = configurator;
        _snapshots = snapshots;
        _adapters = adapters;
        _project = project;
        _isListening = isListening;
        _refreshAdapters = refreshAdapters ?? (() => { });
        _time = time ?? TimeProvider.System;

        _loadingProfile = true;
        try
        {
            HostNumberText = (int.TryParse(project?.Settings.Get(AdapterProfile.HostNumberKey), NumberStyles.None,
                CultureInfo.InvariantCulture, out int stored) ? stored : AdapterProfile.DefaultHostNumber)
                .ToString(CultureInfo.InvariantCulture);

            // Start from what the adapter has now, so Apply with nothing typed changes nothing by surprise.
            if (_configurator.Read(Adapter.Index) is { IsDhcp: false, Addresses: [var first, ..] } now)
            {
                IpText = first.Ip.ToString();
                MaskText = first.Mask.ToString();
                GatewayText = now.Gateway?.ToString() ?? string.Empty;
                DnsText = string.Join(", ", now.Dns);
            }
        }
        finally
        {
            _loadingProfile = false;
        }

        ReadAdapter();
        LoadProfiles();
        Recheck();
    }

    /// <summary>
    /// Asked before anything is run, with the sentence to show (adapter, change, netsh lines); true
    /// means go ahead. Null refuses, as every other confirmation in the app does.
    /// </summary>
    public Func<string, bool>? Confirm { get; set; }

    /// <summary>How long to wait for Windows to report the change before calling it not done.</summary>
    public TimeSpan ReadbackTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How often to look while waiting.</summary>
    public TimeSpan ReadbackInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    public NicInfo Adapter { get; }

    public string AdapterTitle => $"{Adapter.Name}  -  {Adapter.Description}";

    /// <summary>Plan subnets first, then the profiles saved in the project.</summary>
    public ObservableCollection<AdapterProfile> Profiles { get; } = [];

    /// <summary>False for DHCP, which takes no address - the fields grey out.</summary>
    public bool IsAddressMode => Mode != AdapterIpMode.Dhcp;

    public bool HasSaved => SavedText is not null;

    partial void OnModeChanged(AdapterIpMode value) => Recheck();

    partial void OnIpTextChanged(string value) => Recheck();

    partial void OnMaskTextChanged(string value) => Recheck();

    partial void OnGatewayTextChanged(string value) => Recheck();

    partial void OnDnsTextChanged(string value) => Recheck();

    partial void OnSavedTextChanged(string? value)
    {
        OnPropertyChanged(nameof(HasSaved));
        RestoreCommand.NotifyCanExecuteChanged();
    }

    partial void OnHostNumberTextChanged(string value)
    {
        if (_loadingProfile)
        {
            return;
        }

        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int number) && number is > 0 and < 65535)
        {
            _project?.Settings.Set(AdapterProfile.HostNumberKey, number.ToString(CultureInfo.InvariantCulture));
            LoadProfiles();
        }
    }

    partial void OnSelectedProfileChanged(AdapterProfile? value)
    {
        if (value is null)
        {
            return;
        }

        _loadingProfile = true;
        try
        {
            Mode = Mode == AdapterIpMode.Dhcp ? AdapterIpMode.Replace : Mode;
            IpText = value.Ip;
            MaskText = value.Mask;
            GatewayText = value.Gateway;
            DnsText = value.Dns;
            ProfileName = IsFromPlan(value) ? string.Empty : value.Name;
        }
        finally
        {
            _loadingProfile = false;
        }

        Recheck();
    }

    /// <summary>The fields as settings, or null with <see cref="ProblemsText"/> saying why.</summary>
    public AdapterIpSettings? Settings { get; private set; }

    /// <summary>
    /// Re-reads the fields: builds the settings, runs the check, and shows the netsh lines. Every field
    /// change calls this, so what is on screen is always what Apply would run.
    /// </summary>
    public void Recheck()
    {
        if (_loadingProfile)
        {
            return;
        }

        var problems = new List<string>();
        Settings = null;

        if (Mode != AdapterIpMode.Dhcp)
        {
            IPAddress? ip = Address(IpText, "address", required: true, problems);
            IPAddress? mask = Address(MaskText, "mask", required: true, problems);
            IPAddress? gateway = Address(GatewayText, "gateway", required: false, problems);
            var dns = new List<IPAddress>();

            foreach (string part in DnsText.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
            {
                if (Address(part, "DNS server", required: true, problems) is { } server)
                {
                    dns.Add(server);
                }
            }

            if (ip is not null && mask is not null && problems.Count == 0)
            {
                Settings = new AdapterIpSettings { Ip = ip, Mask = mask, Gateway = gateway, Dns = dns };
            }
        }

        var warnings = new List<string>();

        if (problems.Count == 0)
        {
            AdapterIpCheck check = AdapterIpCheck.Of(
                Mode, Settings, _configurator.Read(Adapter.Index), _adapters(), PlannedAddresses());
            problems.AddRange(check.Problems);
            warnings.AddRange(check.Warnings);
        }

        if (_isListening())
        {
            problems.Add("Stop the listener first (Watch or Serve). It is bound to this adapter's address, which is "
                + "about to change.");
        }

        ProblemsText = problems.Count == 0 ? null : string.Join(Environment.NewLine, problems.Select(p => "- " + p));
        WarningsText = warnings.Count == 0 ? null : string.Join(Environment.NewLine, warnings.Select(w => "- " + w));

        CommandsText = problems.Count == 0 && (Settings is not null || Mode == AdapterIpMode.Dhcp)
            ? NetshScript.Display(NetshScript.For(Adapter.Index, Mode, Settings))
            : string.Empty;

        CanApplyChange = problems.Count == 0 && (Settings is not null || Mode == AdapterIpMode.Dhcp);
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync(CancellationToken cancellationToken)
    {
        Recheck();
        if (!CanApplyChange)
        {
            return;
        }

        IReadOnlyList<string> lines = NetshScript.For(Adapter.Index, Mode, Settings);
        string change = Mode switch
        {
            AdapterIpMode.Dhcp => "back to DHCP",
            AdapterIpMode.AddAddress => $"add {Settings}",
            _ => $"set to {Settings}",
        };

        string question = $"Change {Adapter.Name}: {change}?"
            + Environment.NewLine + Environment.NewLine
            + NetshScript.Display(lines)
            + Environment.NewLine + Environment.NewLine
            + "Windows may ask for administrator rights. The settings it has now are kept on this laptop, so "
            + "Restore can put them back.";

        if (Confirm is not { } confirm || !confirm(question))
        {
            SetStatus("Not confirmed. Nothing was changed.", bad: false);
            return;
        }

        AdapterSnapshot? before = _configurator.Read(Adapter.Index);

        // Only the first change is kept: the second would save the first change as "the way it was".
        if (before is not null && _snapshots.Get(before.Name) is null)
        {
            _snapshots.Set(before);
        }

        AdapterIpMode mode = Mode;
        AdapterIpSettings? settings = Settings;

        await RunAsync(
            lines,
            $"{Adapter.Name}: {change}",
            now => AdapterReadback.Mismatch(mode, settings, now),
            cancellationToken).ConfigureAwait(true);
    }

    private bool CanApply() => !IsBusy && CanApplyChange;

    /// <summary>Puts the adapter back to the settings saved before NetControl first changed it.</summary>
    [RelayCommand(CanExecute = nameof(CanRestore))]
    private async Task RestoreAsync(CancellationToken cancellationToken)
    {
        if (_snapshots.Get(Adapter.Name) is not { } saved)
        {
            return;
        }

        if (_isListening())
        {
            SetStatus("Stop the listener first (Watch or Serve). It is bound to this adapter's address.", bad: true);
            return;
        }

        IReadOnlyList<string> lines = NetshScript.Restore(saved with { Index = Adapter.Index });

        string question = $"Put {Adapter.Name} back to {saved.Summary}?"
            + Environment.NewLine + Environment.NewLine
            + NetshScript.Display(lines);

        if (Confirm is not { } confirm || !confirm(question))
        {
            SetStatus("Not confirmed. Nothing was changed.", bad: false);
            return;
        }

        bool done = await RunAsync(
            lines,
            $"{Adapter.Name}: restored to {saved.Summary}",
            now => AdapterReadback.Restored(saved, now),
            cancellationToken).ConfigureAwait(true);

        if (done)
        {
            _snapshots.Clear(Adapter.Name);
            ReadAdapter();
        }
    }

    private bool CanRestore() => !IsBusy && HasSaved;

    /// <summary>Saves the fields as a profile in the project, under <see cref="ProfileName"/>, replacing one of that name.</summary>
    [RelayCommand(CanExecute = nameof(CanSaveProfile))]
    private void SaveProfile()
    {
        if (_project is null || Settings is null)
        {
            SetStatus("Type a valid address and mask first.", bad: true);
            return;
        }

        string name = ProfileName.Trim();
        var profile = new AdapterProfile(name, IpText.Trim(), MaskText.Trim(), GatewayText.Trim(), DnsText.Trim());

        List<AdapterProfile> saved = [.. AdapterProfile.Load(_project.Settings).Where(p => p.Name != name), profile];
        AdapterProfile.Save(_project.Settings, saved);

        LoadProfiles();
        SelectedProfile = Profiles.FirstOrDefault(p => p.Name == name);
        SetStatus($"Saved \"{name}\" in the project.", bad: false);
    }

    private bool CanSaveProfile() => _project is not null && !string.IsNullOrWhiteSpace(ProfileName);

    /// <summary>Deletes the selected saved profile. The plan's own subnets cannot be deleted - they follow the plan.</summary>
    [RelayCommand(CanExecute = nameof(CanDeleteProfile))]
    private void DeleteProfile()
    {
        if (_project is null || SelectedProfile is not { } profile || IsFromPlan(profile))
        {
            return;
        }

        AdapterProfile.Save(_project.Settings, AdapterProfile.Load(_project.Settings).Where(p => p.Name != profile.Name));
        LoadProfiles();
        SetStatus($"Deleted \"{profile.Name}\".", bad: false);
    }

    private bool CanDeleteProfile() => _project is not null && SelectedProfile is { } profile && !IsFromPlan(profile);

    /// <summary>Forgets the saved "before" settings without changing the adapter - when they are not wanted back.</summary>
    [RelayCommand]
    private void ForgetSaved()
    {
        _snapshots.Clear(Adapter.Name);
        ReadAdapter();
    }

    private async Task<bool> RunAsync(
        IReadOnlyList<string> lines,
        string what,
        Func<AdapterSnapshot?, string?> mismatch,
        CancellationToken cancellationToken)
    {
        // Changing this PC's address announces it on the wire (gratuitous ARP, duplicate address
        // detection), so listen-only refuses it like any other transmission.
        if (NetworkGuard.Shared.ListenOnly)
        {
            NetworkGuard.Shared.Ledger.Add(new SentPacket(DateTimeOffset.UtcNow, TransmitKind.AdapterChange, Adapter.Name, what, 0, Blocked: true));
            SetStatus("Listen-only mode is on, so this PC's address was not changed. Turn Listen only off to change it.", bad: true);
            return false;
        }

        IsBusy = true;
        SetStatus("Changing the adapter...", bad: false);

        try
        {
            NetshRun run = await _configurator.RunAsync(lines, cancellationToken).ConfigureAwait(true);

            if (!run.Started)
            {
                SetStatus(run.Message ?? "netsh did not run. Nothing was changed.", bad: true);
                Record(EventSeverity.Warn, $"Adapter change not made - {what}. {run.Message}", lines);
                return false;
            }

            // Windows takes a moment - duplicate address detection alone is about a second - so the
            // adapter is read until it agrees or the wait runs out.
            string? wrong = mismatch(_configurator.Read(Adapter.Index));
            DateTimeOffset giveUp = _time.GetUtcNow() + ReadbackTimeout;

            while (wrong is not null && _time.GetUtcNow() < giveUp)
            {
                await Task.Delay(ReadbackInterval, _time, cancellationToken).ConfigureAwait(true);
                wrong = mismatch(_configurator.Read(Adapter.Index));
            }

            _refreshAdapters();
            ReadAdapter();
            Recheck();

            if (wrong is null)
            {
                SetStatus($"Done, and read back: {CurrentText}", bad: false);
                Record(EventSeverity.Info, $"Adapter changed and read back - {what}.", lines);
                return true;
            }

            string output = run.Output.Length == 0 ? string.Empty : $" netsh said: {run.Output}";
            SetStatus($"netsh ran (exit code {run.ExitCode}) but the adapter does not show the change. {wrong}{output}", bad: true);
            Record(EventSeverity.Error, $"Adapter change not confirmed - {what}. {wrong}{output}", lines);
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Record(EventSeverity severity, string message, IReadOnlyList<string> lines)
    {
        try
        {
            _project?.Events.Append(
                severity,
                EventCategory.App,
                message,
                target: Adapter.Name,
                detail: new EventDetail().Add("operation", "adapterAddress").Add("netsh", NetshScript.Display(lines)));
        }
        catch (PersistenceException)
        {
            // The adapter change happened or did not; a read-only project file does not change that,
            // and the status line has already said which.
        }
    }

    private void ReadAdapter()
    {
        CurrentText = _configurator.Read(Adapter.Index)?.Summary ?? "The adapter could not be read.";
        SavedText = _snapshots.Get(Adapter.Name)?.Summary;
    }

    private void LoadProfiles()
    {
        AdapterProfile? selected = SelectedProfile;
        Profiles.Clear();

        int hostNumber = int.TryParse(HostNumberText, NumberStyles.None, CultureInfo.InvariantCulture, out int n)
            ? n : AdapterProfile.DefaultHostNumber;

        if (_project is not null)
        {
            foreach (AdapterProfile profile in AdapterProfile.FromPlan(_project.Devices.All(), hostNumber))
            {
                Profiles.Add(profile);
            }

            foreach (AdapterProfile profile in AdapterProfile.Load(_project.Settings))
            {
                Profiles.Add(profile);
            }
        }

        _loadingProfile = true;
        try
        {
            SelectedProfile = selected is null ? null : Profiles.FirstOrDefault(p => p.Name == selected.Name);
        }
        finally
        {
            _loadingProfile = false;
        }
    }

    private IEnumerable<IPAddress> PlannedAddresses() =>
        _project is null ? [] : _project.Devices.All().Select(d => d.PlannedIp).OfType<IPAddress>();

    private static bool IsFromPlan(AdapterProfile profile) =>
        profile.Name.StartsWith("Plan: ", StringComparison.Ordinal);

    private static IPAddress? Address(string text, string what, bool required, List<string> problems)
    {
        string trimmed = text.Trim();

        if (trimmed.Length == 0)
        {
            if (required)
            {
                problems.Add($"Type the {what}.");
            }

            return null;
        }

        if (IPAddress.TryParse(trimmed, out IPAddress? address)
            && address.AddressFamily == AddressFamily.InterNetwork
            && trimmed.Count(c => c == '.') == 3)
        {
            return address;
        }

        problems.Add($"'{trimmed}' is not an IPv4 {what}.");
        return null;
    }

    private void SetStatus(string text, bool bad)
    {
        Status = text;
        StatusIsBad = bad;
    }
}

/// <summary>The three kinds of change as the window lists them.</summary>
public static class AdapterIpModes
{
    public static IReadOnlyList<AdapterIpModeOption> All { get; } =
    [
        new(AdapterIpMode.Replace, "Replace - this address only (static)"),
        new(AdapterIpMode.AddAddress, "Add - keep the current address too"),
        new(AdapterIpMode.Dhcp, "DHCP - get an address automatically"),
    ];
}

/// <summary>One kind of change and what the list calls it.</summary>
public sealed record AdapterIpModeOption(AdapterIpMode Value, string Label);
