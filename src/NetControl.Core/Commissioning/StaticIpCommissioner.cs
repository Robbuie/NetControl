using System.Net;
using NetControl.Core.Cip;
using NetControl.Core.Enip;

namespace NetControl.Core.Commissioning;

/// <summary>
/// Writes an address into a device and turns BOOTP/DHCP off, then proves it took.
///
/// <para><b>This is the half of the job BOOTP does not do.</b> Serving an address gets a device
/// onto the network until its next power cycle, at which point it asks again, because it is still
/// a BOOTP client. Setting Configuration Control to Static is what makes the address the device's
/// own. A tool that only serves addresses is one somebody has to use twice.</para>
///
/// <para><b>The order is not negotiable.</b> Capability, then attribute 3, then attribute 5, then
/// a fresh read. Several adapters refuse the attribute 5 write while still in a dynamic mode and
/// answer with a status that gives no hint the order was the problem.</para>
///
/// <para><b>The readback is the point, not a formality.</b> A CIP success status means the request
/// was accepted, not that the configuration persisted, and hardware exists that returns success
/// and discards the write. Nothing here reports success on a status code.</para>
///
/// <para>Safety: unicast to the one address in the request, never a broadcast and never a sweep;
/// no reset unless the caller explicitly allowed one; and every step raises progress so the caller
/// can put it in the append-only event log.</para>
/// </summary>
public sealed class StaticIpCommissioner(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>Raised for each step. On the calling thread; a UI marshals.</summary>
    public event EventHandler<CommissionProgressEventArgs>? Progress;

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How many times to go back and look for the device after writing. A device that changes
    /// address drops off and reappears, and some take several seconds over it.
    /// </summary>
    public int VerifyAttempts { get; init; } = 10;

    public TimeSpan VerifyDelay { get; init; } = TimeSpan.FromSeconds(1.5);

    public async Task<CommissionResult> RunAsync(
        StaticIpRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        bool wrote = false;
        bool reset = false;

        try
        {
            bool resetNeeded;

            // Scoped so the session is closed before verification: if the address moved, this
            // connection is about to die with the old one anyway, and a fresh session is the only
            // honest way to ask the device what it thinks its address is.
            using (EnipSession session = await ConnectAsync(request, cancellationToken).ConfigureAwait(false))
            {
                var tcpIp = new TcpIpInterface(session);

                Report(CommissionStep.ReadCapability, "Reading Configuration Capability (attribute 2).");

                CipReading<ConfigCapability> capability =
                    await tcpIp.ReadCapabilityAsync(cancellationToken).ConfigureAwait(false);

                if (!capability.IsSuccess)
                {
                    return Failed(
                        CommissionOutcome.Refused,
                        $"{request.DeviceAddress} would not report its Configuration Capability: "
                            + capability.StatusText,
                        "Nothing was written. If the device is not an EtherNet/IP adapter it has no TCP/IP "
                            + "Interface object to configure.",
                        CommissionStep.ReadCapability);
                }

                ConfigCapability capabilities = capability.Value;
                Report(CommissionStep.ReadCapability, $"Device reports: {capabilities}.");

                // The hard stop, and the reason this read comes first. No write is attempted, so
                // the device is left exactly as it was found.
                if (!capabilities.HasFlag(ConfigCapability.ConfigurationSettable))
                {
                    return Failed(
                        CommissionOutcome.NotSettable,
                        $"{request.DeviceAddress} reports that its interface configuration cannot be set over "
                            + "the network, so nothing was written.",
                        "The address is pinned by rotary or DIP switches on the module. Set them to the "
                            + "software-configurable position - usually 0, or 999 - power-cycle it, and try again.",
                        CommissionStep.ReadCapability);
                }

                resetNeeded = capabilities.HasFlag(ConfigCapability.InterfaceResetNeeded)
                    || request.Quirks.HasFlag(DeviceQuirks.RequiresResetToApply);

                // ---- Attribute 3: this is the step that disables BOOTP/DHCP -----------------
                Report(CommissionStep.WriteConfigMethod,
                    $"Setting Configuration Control (attribute 3) to {request.Method}.");

                CipResponse control = await tcpIp
                    .WriteConfigMethodAsync(request.Method, cancellationToken)
                    .ConfigureAwait(false);

                wrote = true;

                if (!control.IsSuccess)
                {
                    return Failed(
                        CommissionOutcome.Refused,
                        $"{request.DeviceAddress} refused the Configuration Control write: {control.StatusText}",
                        "The interface is unchanged. Some adapters require the module to be idle before they "
                            + "will accept a configuration change.",
                        CommissionStep.WriteConfigMethod,
                        wrote: true);
                }

                // ---- Attribute 5: the addresses ---------------------------------------------
                // Skipped when handing the device back to BOOTP or DHCP: pinning an address into a
                // device that is about to be told to ask for one would be contradictory.
                if (request.Method == ConfigMethod.Static)
                {
                    Report(CommissionStep.WriteConfiguration,
                        $"Writing Interface Configuration (attribute 5): {request.Ip} / {request.Mask}"
                        + (request.Gateway is null ? ", no gateway" : $", gateway {request.Gateway}"));

                    CipResponse configuration = await tcpIp
                        .WriteConfigurationAsync(request.ToInterfaceConfig(), cancellationToken)
                        .ConfigureAwait(false);

                    if (!configuration.IsSuccess)
                    {
                        return Failed(
                            CommissionOutcome.Refused,
                            $"{request.DeviceAddress} accepted Static but refused the address write: "
                                + configuration.StatusText,
                            "The device is now set to Static at the address it already had. Re-read it before "
                                + "power-cycling, so the record says what it is actually holding.",
                            CommissionStep.WriteConfiguration,
                            wrote: true);
                    }
                }

                if (resetNeeded && request.AllowReset)
                {
                    reset = await ResetAsync(session, cancellationToken).ConfigureAwait(false);
                }
            }

            if (resetNeeded && !request.AllowReset)
            {
                // Not treated as success. The writes were accepted into somewhere the device is not
                // reading from yet, and saying "done" here is how a panel comes back up on the old
                // address tomorrow morning.
                return new CommissionResult
                {
                    Outcome = CommissionOutcome.Unverified,
                    Message = $"{request.DeviceAddress} accepted the configuration but will not apply it until "
                        + "the interface is reset, so it has not been verified.",
                    Remediation = "Power-cycle the device and commission it again to confirm, or re-run this with "
                        + "a reset allowed.",
                    WroteToDevice = true,
                    ResetPending = true,
                };
            }

            return await VerifyAsync(request, wrote, reset, cancellationToken).ConfigureAwait(false);
        }
        catch (EnipException ex)
        {
            Report(CommissionStep.Finished, ex.Message, isFailure: true);

            return new CommissionResult
            {
                // Once a write has gone out, a dropped connection is not "unreachable" - it is
                // "unknown", and those are different things to write on a drawing.
                Outcome = wrote ? CommissionOutcome.Unverified : CommissionOutcome.Unreachable,
                Message = ex.Message,
                Remediation = ex.Remediation,
                WroteToDevice = wrote,
                ConnectionDroppedAfterWrite = wrote,
                ResetTheDevice = reset,
            };
        }
    }

    private async Task<EnipSession> ConnectAsync(StaticIpRequest request, CancellationToken cancellationToken)
    {
        Report(CommissionStep.Connect, $"Connecting to {request.DeviceAddress}:{request.Port}.");

        EnipSession session = await EnipSession
            .ConnectAsync(request.DeviceAddress, request.Port, ConnectTimeoutFor(request), cancellationToken)
            .ConfigureAwait(false);

        Report(CommissionStep.Connect, $"Session registered with {request.DeviceAddress}.");
        return session;
    }

    private async Task<bool> ResetAsync(EnipSession session, CancellationToken cancellationToken)
    {
        Report(CommissionStep.Reset, "Device needs a reset to apply the change; sending Identity Reset as allowed.");

        try
        {
            await session
                .SendAsync(
                    new CipRequest { Service = CipService.Reset, Class = CipClass.Identity },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (EnipException)
        {
            // A device resetting drops the connection, frequently before it has answered. That is
            // the expected shape of a successful reset, not a failure to report.
        }

        return true;
    }

    /// <summary>
    /// Goes back and asks the device what it is actually holding.
    ///
    /// <para>Over a new session, at the address it is supposed to have moved to. A device that has
    /// changed address disappears and reappears, so this retries - and a device that never comes
    /// back is <see cref="CommissionOutcome.Unverified"/>, not a failure and certainly not a
    /// success.</para>
    /// </summary>
    private async Task<CommissionResult> VerifyAsync(
        StaticIpRequest request, bool wrote, bool reset, CancellationToken cancellationToken)
    {
        IPAddress at = request.VerifyAddress;
        Report(CommissionStep.Verify, $"Reading the configuration back from {at}.");

        string lastProblem = "it never answered";

        int attempts = VerifyAttemptsFor(request);

        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            if (attempt > 1 || reset)
            {
                await Task.Delay(VerifyDelay, _time, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                using EnipSession check = await EnipSession
                    .ConnectAsync(at, request.Port, ConnectTimeoutFor(request), cancellationToken)
                    .ConfigureAwait(false);

                var tcpIp = new TcpIpInterface(check);

                CipReading<InterfaceConfig> readback =
                    await tcpIp.ReadConfigurationAsync(cancellationToken).ConfigureAwait(false);
                CipReading<ConfigMethod> method =
                    await tcpIp.ReadConfigMethodAsync(cancellationToken).ConfigureAwait(false);

                if (!readback.IsSuccess || !method.IsSuccess)
                {
                    lastProblem = readback.IsSuccess ? method.StatusText : readback.StatusText;
                    continue;
                }

                return Compare(request, readback.Value, method.Value, wrote, reset);
            }
            catch (EnipException ex)
            {
                lastProblem = ex.Message;
                Report(CommissionStep.Verify, $"Attempt {attempt} of {attempts}: {at} is not answering yet.");
            }
        }

        Report(CommissionStep.Finished, $"{at} never came back, so nothing has been verified.", isFailure: true);

        return new CommissionResult
        {
            Outcome = CommissionOutcome.Unverified,
            Message = $"The writes were accepted but {at} could not be read back - {lastProblem}.",
            Remediation = "Most often this laptop has no route to the new address: give your adapter a second IP "
                + "on that subnet and re-check. Otherwise the device may need a power cycle, or it took the "
                + "configuration into volatile memory only.",
            WroteToDevice = wrote,
            ResetTheDevice = reset,
        };
    }

    private CommissionResult Compare(
        StaticIpRequest request, InterfaceConfig actual, ConfigMethod method, bool wrote, bool reset)
    {
        bool addressesMatch = request.Method != ConfigMethod.Static
            || actual.Matches(request.Ip, request.Mask, request.Gateway);

        if (addressesMatch && method == request.Method)
        {
            string what = request.Method == ConfigMethod.Static
                ? $"{actual.Ip} is set statically on the device and survives a re-read. BOOTP/DHCP is off."
                : $"The device is back to {method} and will ask for an address at its next power cycle.";

            Report(CommissionStep.Finished, what);

            return new CommissionResult
            {
                Outcome = CommissionOutcome.Verified,
                Message = what,
                Remediation = request.Method == ConfigMethod.Static
                    ? "Power-cycle the device and re-read it if you want proof it survives one - that is the "
                        + "only test for a configuration written to volatile memory."
                    : null,
                Readback = actual,
                ReportedMethod = method,
                WroteToDevice = wrote,
                ResetTheDevice = reset,
            };
        }

        // The device returned success for every write and is holding something else. This is real
        // hardware behaviour and it is the whole reason the readback exists.
        List<string> differences = [];

        if (!actual.Ip.Equals(request.Ip) && request.Method == ConfigMethod.Static)
        {
            differences.Add($"address: asked for {request.Ip}, holding {actual.Ip}");
        }

        if (!actual.Mask.Equals(request.Mask) && request.Method == ConfigMethod.Static)
        {
            differences.Add($"mask: asked for {request.Mask}, holding {actual.Mask}");
        }

        if (request.Gateway is { } gateway && !actual.Gateway.Equals(gateway)
            && request.Method == ConfigMethod.Static)
        {
            differences.Add($"gateway: asked for {gateway}, holding {actual.Gateway}");
        }

        if (method != request.Method)
        {
            differences.Add($"method: asked for {request.Method}, reporting {method}");
        }

        string message = $"{request.VerifyAddress} accepted the writes and is holding something else - "
            + string.Join("; ", differences) + ".";

        Report(CommissionStep.Finished, message, isFailure: true);

        return new CommissionResult
        {
            Outcome = CommissionOutcome.Mismatch,
            Message = message,
            Remediation = "The device reported success and did not apply the change. Try again; if it repeats, "
                + "the module's configuration may be locked or its firmware may need updating. Do not record "
                + "this device as commissioned.",
            Readback = actual,
            ReportedMethod = method,
            WroteToDevice = wrote,
            ResetTheDevice = reset,
        };
    }

    private CommissionResult Failed(
        CommissionOutcome outcome,
        string message,
        string? remediation,
        CommissionStep step,
        bool wrote = false)
    {
        Report(step, message, isFailure: true);

        return new CommissionResult
        {
            Outcome = outcome,
            Message = message,
            Remediation = remediation,
            WroteToDevice = wrote,
        };
    }

    /// <summary>
    /// A device known to have a slow stack gets three times the connect timeout. Learned from an
    /// earlier attempt or ticked by hand - see <see cref="DeviceQuirks.SlowResponses"/>.
    /// </summary>
    private TimeSpan ConnectTimeoutFor(StaticIpRequest request) =>
        request.Quirks.HasFlag(DeviceQuirks.SlowResponses) ? ConnectTimeout * 3 : ConnectTimeout;

    /// <summary>And twice as long to come back after the write.</summary>
    private int VerifyAttemptsFor(StaticIpRequest request) =>
        request.Quirks.HasFlag(DeviceQuirks.SlowResponses) ? VerifyAttempts * 2 : VerifyAttempts;

    private void Report(CommissionStep step, string message, bool isFailure = false) =>
        Progress?.Invoke(this, new CommissionProgressEventArgs(step, message, isFailure));
}
