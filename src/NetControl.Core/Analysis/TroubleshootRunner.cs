using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using NetControl.Core.Diagnostics;
using NetControl.Core.Interfaces;
using NetControl.Core.Reachability;
using NetControl.Core.Safety;

namespace NetControl.Core.Analysis;

/// <summary>
/// Gathers the facts <see cref="Troubleshooter"/> judges: reads that cost nothing on the wire
/// first (adapters, UDP/67, the firewall, this PC's counters, the ARP table), then - only when asked
/// and only outside listen-only mode - one ping to the gateway and one (retried once) to each
/// planned device on this PC's own subnets, at most <see cref="MaxPlanPings"/>, four at a time.
///
/// <para>Each source is a delegate so a test can supply any combination of facts without a network.</para>
/// </summary>
public sealed class TroubleshootRunner
{
    public const int MaxPlanPings = 64;

    private const int InFlight = 4;

    private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(1);

    private readonly IPinger _pinger;
    private readonly IArpLookup _arp;
    private readonly NetworkGuard _guard;
    private readonly TimeProvider _time;

    public TroubleshootRunner(IPinger pinger, IArpLookup arp, NetworkGuard? guard = null, TimeProvider? timeProvider = null)
    {
        _pinger = pinger ?? throw new ArgumentNullException(nameof(pinger));
        _arp = arp ?? throw new ArgumentNullException(nameof(arp));
        _guard = guard ?? NetworkGuard.Shared;
        _time = timeProvider ?? TimeProvider.System;
    }

    public Func<int, PortConflictReport> ReadPort { get; init; } = PortConflictDetector.Inspect;

    public Func<int, FirewallStatus> ReadFirewall { get; init; } = FirewallCheck.Inspect;

    public Func<int, NicCounters?> ReadCounters { get; init; } = ReadCountersHere;

    public Func<NicInfo, bool?> ReadOwnDuplicate { get; init; } = OwnAddressIsDuplicate;

    public async Task<(TroubleshootInputs Inputs, IReadOnlyList<NetworkFinding> Findings)> RunAsync(
        TroubleshootRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        NicInfo? nic = request.Adapter;
        NetTrace.Log.Info($"Troubleshoot started on {(nic is null ? "no adapter" : $"[{nic.Index}] {nic.Name}")}"
            + $"{(request.IncludeActiveChecks ? ", with ping checks" : string.Empty)}.");

        progress?.Report("Checking what else on this PC holds UDP/67, and the firewall...");
        PortConflictReport? port = Try(() => ReadPort(67));
        FirewallStatus? firewall = Try(() => ReadFirewall(67));

        NicCounters? before = null;
        NicCounters? after = null;
        bool? duplicate = null;

        if (nic is { IsUp: true })
        {
            duplicate = Try(() => ReadOwnDuplicate(nic));
            before = Try(() => ReadCounters(nic.Index));

            if (before is not null && request.CounterWindow > TimeSpan.Zero)
            {
                progress?.Report($"Watching this PC's port for {request.CounterWindow.TotalSeconds:0} s...");
                await Task.Delay(request.CounterWindow, _time, cancellationToken).ConfigureAwait(false);
                after = Try(() => ReadCounters(nic.Index));
            }
        }

        progress?.Report("Reading this PC's ARP table...");
        IReadOnlyList<ArpEntry> arp = _arp.IsSupported ? (Try(_arp.Snapshot) ?? []) : [];

        bool listenOnly = _guard.ListenOnly;
        bool active = request.IncludeActiveChecks && !listenOnly && nic is { CanServe: true };
        PingOutcome? gateway = null;
        var planPings = new List<(PlannedAddress, PingOutcome)>();

        if (active)
        {
            try
            {
                if (nic!.Gateway is { } gw && !gw.Equals(IPAddress.Any) && UnicastTarget.TryCheck(gw, nic.Mask, out _))
                {
                    progress?.Report($"Pinging the gateway {gw}...");
                    gateway = await PingTwiceAsync(gw, cancellationToken).ConfigureAwait(false);
                }

                List<PlannedAddress> reachable = [.. OnThisPcsSubnets(nic, request.Plan).Take(MaxPlanPings)];

                if (reachable.Count > 0)
                {
                    progress?.Report($"Pinging {reachable.Count} planned device(s)...");
                    using var slots = new SemaphoreSlim(InFlight);
                    var results = await Task.WhenAll(reachable.Select(async device =>
                    {
                        await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
                        try
                        {
                            return (device, await PingTwiceAsync(device.Address!, cancellationToken).ConfigureAwait(false));
                        }
                        finally
                        {
                            slots.Release();
                        }
                    })).ConfigureAwait(false);

                    planPings.AddRange(results);
                }
            }
            catch (TransmitBlockedException)
            {
                // Listen-only was switched on mid-run: report what was gathered, with the pings marked skipped.
                active = false;
                listenOnly = true;
            }
        }

        var inputs = new TroubleshootInputs
        {
            Adapter = nic,
            AllAdapters = request.AllAdapters,
            DhcpPort = port,
            Firewall = firewall,
            CountersBefore = before,
            CountersAfter = after,
            OwnAddressDuplicate = duplicate,
            Plan = request.Plan,
            Arp = arp,
            Conflicts = request.Conflicts,
            Traffic = request.Traffic,
            UnplannedAskers = request.UnplannedAskers,
            ListenOnly = listenOnly,
            ActiveChecksRan = active,
            GatewayPing = active ? gateway : null,
            PlanPings = active ? planPings : [],
        };

        IReadOnlyList<NetworkFinding> findings = Troubleshooter.Assess(inputs);
        NetTrace.Log.Info($"Troubleshoot finished: {findings.Count(f => f.Severity == FindingSeverity.Error)} problem(s), "
            + $"{findings.Count(f => f.Severity == FindingSeverity.Warn)} to check.");

        foreach (NetworkFinding finding in findings.Where(f => f.Severity >= FindingSeverity.Warn))
        {
            NetTrace.Log.Info($"  {finding.SeverityText}: {finding.Title} - {finding.Detail}");
        }

        return (inputs, findings);
    }

    /// <summary>The plan's addressed devices that sit on one of this adapter's own subnets.</summary>
    public static IEnumerable<PlannedAddress> OnThisPcsSubnets(NicInfo nic, IEnumerable<PlannedAddress> plan)
    {
        ArgumentNullException.ThrowIfNull(nic);
        ArgumentNullException.ThrowIfNull(plan);

        List<NicInfo> addresses = [.. nic.PerAddress()];

        foreach (PlannedAddress device in plan)
        {
            if (device.Address is null || nic.Owns(device.Address))
            {
                continue;
            }

            foreach (NicInfo address in addresses)
            {
                if (Ipv4Subnet.TryCreate(address.IPv4, address.Mask, out Ipv4Subnet subnet)
                    && subnet.Contains(device.Address)
                    && UnicastTarget.TryCheck(device.Address, address.Mask, out _))
                {
                    yield return device;
                    break;
                }
            }
        }
    }

    /// <summary>
    /// One echo, and a second only if the first was lost - the first echo to an address not in the
    /// ARP cache is often lost while ARP resolves.
    /// </summary>
    private async Task<PingOutcome> PingTwiceAsync(IPAddress address, CancellationToken cancellationToken)
    {
        PingOutcome first = await _pinger.PingAsync(address, PingTimeout, cancellationToken).ConfigureAwait(false);
        if (first.Replied || first.Status == PingStatus.Unreachable)
        {
            return first;
        }

        PingOutcome second = await _pinger.PingAsync(address, PingTimeout, cancellationToken).ConfigureAwait(false);
        return second with { Attempts = 2 };
    }

    private static NicCounters? ReadCountersHere(int index) =>
        OperatingSystem.IsWindows() ? ReadCountersWindows(index) : null;

    [SupportedOSPlatform("windows")]
    private static NicCounters? ReadCountersWindows(int index) => NicCounters.Read(index);

    private static T? Try<T>(Func<T?> read)
        where T : class
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            NetTrace.Log.Warn("Troubleshoot: a check could not be read.", ex);
            return null;
        }
    }

    private static bool? Try(Func<bool?> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            NetTrace.Log.Warn("Troubleshoot: duplicate-address state could not be read.", ex);
            return null;
        }
    }

    private static bool? OwnAddressIsDuplicate(NicInfo nic) =>
        OperatingSystem.IsWindows() ? ReadDuplicate(nic) : null;

    [SupportedOSPlatform("windows")]
    private static bool? ReadDuplicate(NicInfo nic)
    {
        foreach (NetworkInterface candidate in NetworkInterface.GetAllNetworkInterfaces())
        {
            IPInterfaceProperties properties = candidate.GetIPProperties();
            if (properties.GetIPv4Properties()?.Index != nic.Index)
            {
                continue;
            }

            foreach (UnicastIPAddressInformation address in properties.UnicastAddresses)
            {
                if (address.Address.Equals(nic.IPv4))
                {
                    return address.DuplicateAddressDetectionState == DuplicateAddressDetectionState.Duplicate;
                }
            }
        }

        return null;
    }
}
