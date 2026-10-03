using System.Collections.ObjectModel;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetControl.Core;
using NetControl.Core.Capture;
using NetControl.Core.Interfaces;
using NetControl.Core.Persistence;
using NetControl.Core.Plan;
using NetControl.Core.Profinet;

namespace NetControl.App.ViewModels;

/// <summary>
/// The PROFINET tab: find devices with DCP Identify, and give one of them a station name or an
/// address - the job Siemens' Primary Setup Tool and PRONETA do.
///
/// <para><b>The same contract as Set static.</b> Identify is discovery and is the only thing sent to
/// more than one device. A name, an address or a flash goes to the one MAC selected in the list, only
/// after <see cref="ConfirmWrite"/> has named that device and said yes - a null confirmation refuses -
/// and a name or address is then read back with a DCP Get, and only a readback that shows the new
/// value is reported as done. Every step is a row in the record.</para>
///
/// <para>Needs Npcap, as DCP is Ethernet, not IP. Without it the tab says what to install.</para>
/// </summary>
public sealed partial class ProfinetViewModel : ObservableObject
{
    private const string Filter = "ether proto 0x8892";

    private readonly Func<ProjectStore> _project;
    private readonly Func<NicInfo?> _adapter;
    private readonly ICaptureProvider _capture;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAvailable))]
    [NotifyPropertyChangedFor(nameof(AvailabilityText))]
    [NotifyPropertyChangedFor(nameof(Remediation))]
    [NotifyCanExecuteChangedFor(nameof(IdentifyCommand))]
    private CaptureAvailability? _availability;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(IdentifyCommand))]
    [NotifyCanExecuteChangedFor(nameof(SetNameCommand))]
    [NotifyCanExecuteChangedFor(nameof(SetIpCommand))]
    [NotifyCanExecuteChangedFor(nameof(FlashCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReadBackCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SetNameCommand))]
    [NotifyCanExecuteChangedFor(nameof(SetIpCommand))]
    [NotifyCanExecuteChangedFor(nameof(FlashCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReadBackCommand))]
    private DcpDevice? _selected;

    [ObservableProperty]
    private string _newName = string.Empty;

    [ObservableProperty]
    private string _newIp = string.Empty;

    [ObservableProperty]
    private string _newMask = "255.255.255.0";

    [ObservableProperty]
    private string _newGateway = string.Empty;

    /// <summary>Keep across a power cycle. On by default; off is for a quick test on a bench.</summary>
    [ObservableProperty]
    private bool _permanent = true;

    [ObservableProperty]
    private string? _status;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _errorRemediation;

    public ProfinetViewModel(Func<ProjectStore> project, Func<NicInfo?> adapter, ICaptureProvider capture)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(capture);

        _project = project;
        _adapter = adapter;
        _capture = capture;
    }

    /// <summary>
    /// Asked before every Set with a sentence naming the device and the change. Null refuses: a
    /// write to plant equipment with nobody to say yes to it is not one that happens.
    /// </summary>
    public Func<string, bool>? ConfirmWrite { get; set; }

    public ObservableCollection<DcpDevice> Devices { get; } = [];

    public bool IsAvailable => Availability?.IsAvailable == true;

    public string AvailabilityText => Availability?.Headline ?? "Not checked yet.";

    public string? Remediation => Availability?.Remediation;

    /// <summary>How long Identify listens for answers.</summary>
    public TimeSpan IdentifyWindow { get; init; } = TimeSpan.FromSeconds(3);

    [RelayCommand]
    public void CheckAvailability() => Availability = _capture.Check();

    /// <summary>DCP Identify All: one multicast, and every PROFINET device on the segment answers.</summary>
    [RelayCommand(CanExecute = nameof(CanIdentify))]
    private async Task IdentifyAsync(CancellationToken cancellationToken)
    {
        await RunAsync("Identifying PROFINET devices...", async client =>
        {
            IReadOnlyList<DcpDevice> found = await client.IdentifyAsync(IdentifyWindow, cancellationToken).ConfigureAwait(true);

            MacAddress? keep = Selected?.Mac;
            Devices.Clear();
            foreach (DcpDevice device in found)
            {
                Devices.Add(device);
            }

            Selected = Devices.FirstOrDefault(d => d.Mac == keep);

            int unnamed = found.Count(d => !d.HasName);
            int conflicts = found.Count(d => d.AddressConflict);
            Status = $"{found.Count} PROFINET device(s) answered"
                + (unnamed > 0 ? $", {unnamed} without a station name" : string.Empty)
                + (conflicts > 0 ? $", {conflicts} reporting an address conflict" : string.Empty) + ".";

            Record(conflicts > 0 ? EventSeverity.Warn : EventSeverity.Info, null, $"DCP Identify: {Status}");
        }).ConfigureAwait(true);
    }

    /// <summary>Reads the selected device again with a DCP Get.</summary>
    [RelayCommand(CanExecute = nameof(CanWrite))]
    private async Task ReadBackAsync(CancellationToken cancellationToken)
    {
        DcpDevice target = Selected!;

        await RunAsync($"Reading {target.Mac}...", async client =>
        {
            DcpDevice? now = await client.GetAsync(target.Mac, cancellationToken).ConfigureAwait(true);
            if (now is null)
            {
                SetError($"{target.Mac} did not answer a DCP Get.", "Check it is on this adapter's segment, and Identify again.");
                return;
            }

            Replace(now);
            Status = $"{target.Mac}: '{now.NameOfStation}', {now.Describe()}.";
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanWrite))]
    private async Task SetNameAsync(CancellationToken cancellationToken)
    {
        DcpDevice target = Selected!;
        string name = NewName.Trim();

        if (DcpStationName.Problem(name) is { } problem)
        {
            SetError(problem, "Station names are what the controller's configuration finds the device by - copy it from the project.");
            return;
        }

        if (!Confirm(
                $"Set the PROFINET station name of {Label(target)} to '{name}'"
                + (Permanent ? ", permanently" : " until its next power cycle") + "?"
                + Environment.NewLine + Environment.NewLine
                + "A device in data exchange with a controller will drop out of it if its name changes."))
        {
            return;
        }

        await RunAsync($"Setting the name of {target.Mac}...", async client =>
        {
            Record(EventSeverity.Warn, target, $"DCP Set NameOfStation '{name}' on {target.Mac}" + (Permanent ? " (permanent)." : " (temporary)."));

            DcpSetResult result = await client.SetNameAsync(target.Mac, name, Permanent, cancellationToken).ConfigureAwait(true);
            DcpDevice? after = result.Accepted ? await client.GetAsync(target.Mac, cancellationToken).ConfigureAwait(true) : null;

            Finish(target, result, after, after?.NameOfStation == name, $"station name '{name}'");
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanWrite))]
    private async Task SetIpAsync(CancellationToken cancellationToken)
    {
        DcpDevice target = Selected!;

        if (!PlanValidation.TryParseIPv4(NewIp.Trim(), out IPAddress? ip)
            || !PlanValidation.TryParseIPv4(NewMask.Trim(), out IPAddress? mask))
        {
            SetError("Type an address and a mask as four numbers each.", null);
            return;
        }

        IPAddress? gateway = null;
        if (!string.IsNullOrWhiteSpace(NewGateway) && !PlanValidation.TryParseIPv4(NewGateway.Trim(), out gateway))
        {
            SetError($"'{NewGateway.Trim()}' is not an IPv4 address.", "Leave the gateway blank for none.");
            return;
        }

        if (DcpClient.IpProblem(ip, mask, gateway) is { } problem)
        {
            SetError(problem, null);
            return;
        }

        IPAddress address = ip;
        IPAddress netmask = mask;
        string what = $"{address}/{netmask}" + (gateway is null ? string.Empty : $" gateway {gateway}");

        if (!Confirm(
                $"Set the address of {Label(target)} to {what}"
                + (Permanent ? ", permanently" : " until its next power cycle") + "?"
                + Environment.NewLine + Environment.NewLine
                + "A controller normally assigns this itself at connection time; setting it by hand is for a device "
                + "commissioned without one, or one whose address the controller's project does not set."))
        {
            return;
        }

        await RunAsync($"Setting the address of {target.Mac}...", async client =>
        {
            Record(EventSeverity.Warn, target, $"DCP Set IP {what} on {target.Mac}" + (Permanent ? " (permanent)." : " (temporary)."));

            DcpSetResult result = await client.SetIpAsync(target.Mac, address, netmask, gateway, Permanent, cancellationToken).ConfigureAwait(true);
            DcpDevice? after = result.Accepted ? await client.GetAsync(target.Mac, cancellationToken).ConfigureAwait(true) : null;

            bool holds = after is not null && address.Equals(after.Address) && netmask.Equals(after.Mask)
                && (gateway ?? IPAddress.Any).Equals(after.Gateway ?? IPAddress.Any);

            Finish(target, result, after, holds, $"address {what}");
        }).ConfigureAwait(true);
    }

    /// <summary>Flashes the selected device's LED for a few seconds. Stores nothing, so it is not confirmed.</summary>
    [RelayCommand(CanExecute = nameof(CanWrite))]
    private async Task FlashAsync(CancellationToken cancellationToken)
    {
        DcpDevice target = Selected!;

        await RunAsync($"Flashing {target.Mac}...", async client =>
        {
            DcpSetResult result = await client.SignalAsync(target.Mac, cancellationToken).ConfigureAwait(true);
            Status = result.Accepted
                ? $"{Label(target)} is flashing its LED."
                : $"{target.Mac} did not flash: {result.Text}.";
            Record(EventSeverity.Info, target, $"DCP Signal (flash LED) to {target.Mac}: {result.Text}.");
        }).ConfigureAwait(true);
    }

    partial void OnSelectedChanged(DcpDevice? value)
    {
        if (value is null)
        {
            return;
        }

        // Start the boxes from what the device holds, so changing one thing does not mean retyping the rest.
        NewName = value.NameOfStation;
        NewIp = value.HasNoAddress ? string.Empty : value.Address?.ToString() ?? string.Empty;
        NewMask = value.Mask is { } mask && !mask.Equals(IPAddress.Any) ? mask.ToString() : "255.255.255.0";
        NewGateway = value.Gateway is { } gw && !gw.Equals(IPAddress.Any) ? gw.ToString() : string.Empty;
    }

    private static string Label(DcpDevice device) =>
        device.HasName ? $"'{device.NameOfStation}' ({device.Mac})" : $"{device.Mac} (no station name)";

    private bool CanIdentify() => !IsBusy && IsAvailable;

    private bool CanWrite() => CanIdentify() && Selected is not null;

    private bool Confirm(string question) => ConfirmWrite?.Invoke(question) == true;

    /// <summary>
    /// The end of a Set: accepted and read back holding the value is the only success; everything
    /// else says which of the three ways it was not.
    /// </summary>
    private void Finish(DcpDevice target, DcpSetResult result, DcpDevice? after, bool holds, string what)
    {
        if (after is not null)
        {
            Replace(after);
        }

        if (!result.Accepted)
        {
            string message = $"{target.Mac} refused the {what}: {result.Text}.";
            SetError(message, result.ErrorCode is 5 or 6
                ? "Stop the controller's connection to it first, or change the name in the controller's project instead."
                : null);
            Status = message;
            Record(EventSeverity.Error, target, message);
            return;
        }

        if (!holds)
        {
            string message = after is null
                ? $"{target.Mac} accepted the {what} and then did not answer the readback - nothing is verified."
                : $"{target.Mac} accepted the {what} but reads back {after.NameOfStation} / {after.Describe()}.";
            SetError(message, "Read it again in a few seconds; some devices apply a change only after a moment.");
            Status = message;
            Record(EventSeverity.Error, target, message);
            return;
        }

        string verified = $"Verified: {target.Mac} now holds the {what}.";
        Status = verified;
        Record(EventSeverity.Info, after, verified);
    }

    private void Replace(DcpDevice device)
    {
        for (int i = 0; i < Devices.Count; i++)
        {
            if (Devices[i].Mac == device.Mac)
            {
                Devices[i] = device;
                Selected = device;
                return;
            }
        }

        Devices.Add(device);
        Selected = device;
    }

    /// <summary>Opens the adapter, runs one operation with a DCP client on it, and closes it again.</summary>
    private async Task RunAsync(string starting, Func<DcpClient, Task> work)
    {
        ClearError();

        if (_adapter() is not { } nic)
        {
            SetError("No adapter is selected.", "DCP goes out of exactly one adapter, chosen at the top of the window.");
            return;
        }

        IsBusy = true;
        Status = starting;

        try
        {
            using IFrameChannel channel = _capture.Open(nic, Filter, promiscuous: false);
            await work(new DcpClient(channel)).ConfigureAwait(true);
        }
        catch (CaptureException ex)
        {
            SetError(ex.Message, ex.Remediation);
            Status = ex.Message;
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Record(EventSeverity severity, DcpDevice? device, string message)
    {
        try
        {
            _project().Events.Append(
                severity,
                EventCategory.Profinet,
                message,
                target: device?.Mac.ToString(),
                detail: device is null ? null : new EventDetail()
                    .Add("name", device.NameOfStation)
                    .Add("address", device.Address?.ToString())
                    .Add("id", device.IdText));
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
