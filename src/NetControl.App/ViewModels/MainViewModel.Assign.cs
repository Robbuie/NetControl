using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using NetControl.App.Serving;
using NetControl.Core;
using NetControl.Core.Commissioning;
using NetControl.Core.Dhcp;
using NetControl.Core.Interfaces;
using NetControl.Core.Persistence;

namespace NetControl.App.ViewModels;

/// <summary>
/// Assign IP: the Rockwell BOOTP/DHCP tool's workflow as one gesture on a live request.
///
/// <para>A device asks; you type an address; the mask and gateway come from the adapter it asked on;
/// it is served; and - ticked by default - BOOTP/DHCP is turned off so the address is the device's
/// own. Under the hood it is exactly the plan, Serve and Set static the rest of the tool already
/// does, chained, so the record, the readback and Verified mean the same thing they always did.</para>
///
/// <para><b>Serve is still explicit.</b> Pressing Assign in the dialog is the arming: it names one
/// MAC and one address, on the adapter shown in the dialog, and the plan is what Serve answers from,
/// so nothing beyond the devices already in the plan can be served because of it.</para>
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>How long to wait for the device to ask again. DHCP clients back off to ~64 s between tries.</summary>
    internal static TimeSpan AssignServeTimeout { get; set; } = TimeSpan.FromSeconds(150);

    /// <summary>How long a device that has just taken an address gets to bring up TCP/44818.</summary>
    internal static TimeSpan AssignPortTimeout { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>The MAC an Assign is waiting to see served, and the wait itself. One at a time.</summary>
    private (MacAddress Mac, TaskCompletionSource<IPAddress> Served)? _assignWait;

    /// <summary>
    /// False while Assign itself is switching the listener from Watch to Serve, which passes through
    /// Stopped on the way and must not read as the user pressing Stop.
    /// </summary>
    private bool _assignWaitCancellable;

    /// <summary>True while an Assign is in progress, from the dialog closing to the readback.</summary>
    [ObservableProperty]
    private bool _isAssigning;

    /// <summary>
    /// What the Assign IP dialog opens with, or null - with the reason in the error banner - when
    /// there is no adapter to assign on.
    /// </summary>
    public AssignDefaults? AssignDefaultsFor(LogEntryViewModel entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (entry.Mac.IsEmpty)
        {
            Log.AddNotice("That row is not about a device, so there is nothing to assign an address to.");
            return null;
        }

        if (IsAssigning || Plan.IsCommissioning)
        {
            SetError("An address is already being assigned.", "Wait for it to finish - one device at a time.");
            return null;
        }

        if (InterfaceBar.SelectedAdapter is not { Nic: { } nic } adapter || !nic.CanServe)
        {
            SetError(
                "Select the adapter that is plugged into the device's network first.",
                "It needs a real IPv4 address (not 169.254.x.x): the reply is sent from it, and the mask and "
                    + "gateway are taken from it.");
            return null;
        }

        DeviceRowViewModel? existing = Plan.Rows.FirstOrDefault(row => row.Mac == entry.Mac);

        return new AssignDefaults(
            entry.Mac,
            entry.VendorText,
            adapter.DisplayName,
            nic.IPv4?.ToString() ?? string.Empty,
            [.. nic.PerAddress().Select(SubnetOf).OfType<Ipv4Subnet>()],
            Ip: existing?.IpText ?? string.Empty,
            Mask: existing is { MaskText.Length: > 0 } ? existing.MaskText : nic.Mask?.ToString() ?? "255.255.255.0",
            Gateway: existing is { GatewayText.Length: > 0 } ? existing.GatewayText : nic.Gateway?.ToString() ?? string.Empty);
    }

    /// <summary>
    /// Plans the device at the given address, serves it, and - when asked - turns BOOTP/DHCP off and
    /// reads the result back. Narrates every step in the live log, and records it in the project.
    /// </summary>
    public async Task AssignAsync(MacAddress mac, AddressAssignment assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);

        if (mac.IsEmpty || IsAssigning || Plan.IsCommissioning)
        {
            return;
        }

        ClearError();

        if (InterfaceBar.SelectedAdapter is not { Nic: { } nic } adapter || !nic.CanServe)
        {
            SetError("The selected adapter cannot send a reply.", "Select the adapter on the device's network.");
            return;
        }

        IsAssigning = true;

        // What the listener was doing before, so it can be put back once the device no longer needs
        // serving. A user who was only watching should not find themselves left running a server.
        DhcpServerMode? before = RunState is ServerRunState.Listening ? _server.ActiveOptions?.Mode : null;
        bool startedServing = false;

        try
        {
            // ---- 1. The plan row: the one thing Serve answers from ------------------------------
            DeviceRowViewModel row = Plan.PlanDevice(mac);

            // Mask and gateway first, address last: each assignment commits the row, and a row with
            // an address and no mask is briefly a problem nobody needs recorded.
            row.MaskText = assignment.Mask.ToString();
            row.GatewayText = assignment.Gateway?.ToString() ?? string.Empty;
            row.IpText = assignment.Ip.ToString();

            if (!row.IsServable || row.Id <= 0)
            {
                string why = row.Problem ?? row.ServeNote ?? "the row could not be saved to the project";
                SetError($"{mac} cannot be given {assignment.Ip}: {why}", "Fix the plan row, then try again.");
                Log.AddNotice($"Assign {assignment.Ip} to {mac} stopped before anything was sent: {why}");
                return;
            }

            RecordAssign(
                $"Assign requested: {mac} -> {assignment.Ip} / {assignment.Mask}"
                + (assignment.Gateway is null ? ", no gateway" : $", gateway {assignment.Gateway}")
                + (assignment.MakeStatic ? ", then disable BOOTP/DHCP." : ", serve only."));

            // ---- 2. Serve, on this adapter -------------------------------------------------------
            var served = new TaskCompletionSource<IPAddress>(TaskCreationOptions.RunContinuationsAsynchronously);
            _assignWait = (mac, served);

            bool alreadyServingHere = before == DhcpServerMode.Serve
                && _server.ActiveOptions?.InterfaceIndexFilter == adapter.Index;

            if (!alreadyServingHere)
            {
                if (RunState is ServerRunState.Listening)
                {
                    await StopAsync().ConfigureAwait(true);
                }

                IsServeArmed = true;
                await StartAsync(DhcpServerMode.Serve).ConfigureAwait(true);

                if (RunState is not ServerRunState.Listening)
                {
                    // StartAsync has already put the reason in the banner.
                    Log.AddNotice($"Could not start serving, so {mac} was not given {assignment.Ip}.");
                    return;
                }

                startedServing = true;
            }

            _assignWaitCancellable = true;

            Log.AddNotice(
                $"Waiting for {mac} to ask again - it will be given {assignment.Ip}.",
                "Devices retry on their own, but a DHCP client can wait a minute between tries. Power-cycling "
                    + "the device makes it ask straight away. Stop cancels.");

            Task finished;
            using (var timeout = new CancellationTokenSource())
            {
                finished = await Task
                    .WhenAny(served.Task, Task.Delay(AssignServeTimeout, _time, timeout.Token))
                    .ConfigureAwait(true);
                await timeout.CancelAsync().ConfigureAwait(true);
            }

            if (finished != served.Task || !served.Task.IsCompletedSuccessfully)
            {
                string message = served.Task.IsCanceled
                    ? $"Assign cancelled: the listener stopped before {mac} took {assignment.Ip}."
                    : $"{mac} did not ask again within {AssignServeTimeout.TotalSeconds:0} s, so it has not been given "
                        + $"{assignment.Ip}.";
                string remediation = RunState is ServerRunState.Listening
                    ? "Serve is still running and will answer it whenever it asks - power-cycle the device. When its "
                        + "row says Served, press Set static to turn BOOTP/DHCP off."
                    : "Start Serve, power-cycle the device, then press Set static on its row.";

                SetError(message, remediation);
                Log.AddNotice(message, remediation);
                RecordAssign(message, EventSeverity.Warn);
                startedServing = false;     // Leave it serving: the device may still ask.
                return;
            }

            Log.AddNotice($"{mac} was given {assignment.Ip}.");

            if (!assignment.MakeStatic)
            {
                Log.AddNotice(
                    $"{assignment.Ip} is served, not permanent: {mac} will ask again at its next power cycle.",
                    "Serve is left running so it gets the same address. Set static on its row makes it permanent.");
                startedServing = false;
                return;
            }

            // ---- 3. Wait for the device to come up on its new address ---------------------------
            int port = Plan.CommissionPort;
            Log.AddNotice($"Waiting for {assignment.Ip} to answer on EtherNet/IP (TCP/{port}).");

            bool up = await EnipPortWaiter
                .WaitAsync(assignment.Ip, port, AssignPortTimeout, timeProvider: _time)
                .ConfigureAwait(true);

            if (!up)
            {
                string message = $"{mac} took {assignment.Ip} but never answered on TCP/{port}, so "
                    + "BOOTP/DHCP has not been turned off.";
                const string remediation = "Ping the address. If it answers, the device may not be EtherNet/IP, or it "
                    + "needs longer - press Set static on its row. If it does not, check the cable and that this PC "
                    + "has an address on the device's subnet.";
                SetError(message, remediation);
                Log.AddNotice(message, remediation);
                RecordAssign(message, EventSeverity.Warn);
                return;
            }

            // ---- 4. Set static: the same path as the button, so Verified means what it always has --
            CommissionResult? result = await Plan.CommissionRowAsync(row).ConfigureAwait(true);

            if (result is null)
            {
                Log.AddNotice($"Set static did not run for {mac}. Select its row and press Set static.");
                return;
            }

            if (result.IsVerified)
            {
                Log.AddNotice($"Done: {mac} is {assignment.Ip}, BOOTP/DHCP is off, and the readback agrees.");
            }

            // The commissioner has already put a failure in the banner and the record.
        }
        finally
        {
            _assignWait = null;
            _assignWaitCancellable = false;
            IsAssigning = false;

            if (startedServing && RunState is ServerRunState.Listening)
            {
                await StopAsync().ConfigureAwait(true);

                if (before == DhcpServerMode.Watch)
                {
                    await StartAsync(DhcpServerMode.Watch).ConfigureAwait(true);
                }
                else
                {
                    Log.AddNotice("Serve stopped - it was only started for this device.");
                }
            }
        }
    }

    /// <summary>Called from <see cref="OnReplySent"/>, on the UI thread.</summary>
    private void NoteAssignReply(DhcpReplyEventArgs e)
    {
        if (_assignWait is not { } wait || e.Mac != wait.Mac || e.Assignment is null)
        {
            return;
        }

        // An OFFER is only a proposal: a DHCP client has the address once it has been ACKed. A BOOTP
        // reply has no message type at all, and is the address.
        if (e.Reply.MessageType is null or DhcpMessageType.Ack)
        {
            wait.Served.TrySetResult(e.Assignment.Ip);
        }
    }

    /// <summary>Called when the listener stops: there is nothing left to wait for.</summary>
    private void CancelAssignWait()
    {
        if (_assignWaitCancellable && RunState is not (ServerRunState.Listening or ServerRunState.Starting))
        {
            _assignWait?.Served.TrySetCanceled();
        }
    }

    private void RecordAssign(string message, EventSeverity severity = EventSeverity.Info)
    {
        try
        {
            _project.Events.Append(severity, EventCategory.Dhcp, message);
        }
        catch (PersistenceException ex)
        {
            RecordingIsIncomplete = true;
            Log.AddNotice($"Could not record the assignment: {ex.Message}", ex.Remediation);
        }
    }

    private static Ipv4Subnet? SubnetOf(NicInfo part) =>
        Ipv4Subnet.TryCreate(part.IPv4, part.Mask, out Ipv4Subnet subnet) ? subnet : null;
}
