using NetControl.App.Diagnostics;
using NetControl.App.Serving;
using NetControl.App.ViewModels;
using NetControl.Core.Dhcp;
using NetControl.Core.Interfaces;
using NetControl.Core.Oui;
using Xunit;

namespace NetControl.Tests;

/// <summary>
/// The bar's whole job is to be trusted, so these tests are mostly about the ways it could lie:
/// showing green over something it never measured, keeping a green light after the listener died,
/// or losing the user's adapter because Windows removed it for a second.
/// </summary>
public sealed class InterfaceBarViewModelTests
{
    [Fact]
    public void StartsUnknownWithNothingSelected()
    {
        InterfaceBarViewModel bar = Bar();

        Assert.Null(bar.SelectedAdapter);
        Assert.Equal(ReadinessState.Unknown, bar.Overall);
        Assert.False(bar.CanArmServeMode);
    }

    [Fact]
    public void DoesNotSelectAnAdapterForYou()
    {
        InterfaceBarViewModel bar = Bar();

        bar.ApplyInventory([Nics.Healthy(index: 12), Nics.Healthy(index: 13, address: "10.0.0.5")]);

        Assert.Equal(2, bar.Adapters.Count);
        Assert.Null(bar.SelectedAdapter);
    }

    [Fact]
    public void IsReadyOnlyWhenEveryCheckIsMeasuredAndGood()
    {
        InterfaceBarViewModel bar = Ready();

        Assert.Equal(ReadinessState.Ready, bar.Overall);
        Assert.Contains("will be received", bar.Headline, StringComparison.Ordinal);
    }

    /// <summary>
    /// The rule the whole grade rests on. A firewall we could not read is not a firewall that is
    /// open, and a green light over an unmeasured check is the failure this bar exists to prevent.
    /// </summary>
    [Fact]
    public void AnUnreadableFirewallIsNeverGreen()
    {
        InterfaceBarViewModel bar = Ready();

        bar.ApplyPreflight(new PreflightResult(
            Preflights.Port(PortConflictSeverity.None, "UDP/67 is free."),
            Preflights.Firewall(FirewallVerdict.Unknown, "Could not read the Windows Firewall configuration."),
            Events.At));

        Assert.Equal(ReadinessState.Unknown, bar.Overall);
        Assert.NotEqual(ReadinessState.Ready, bar.Overall);
    }

    [Fact]
    public void AWildcardPortOwnerBlocksAndIsWhatTheHeadlineSays()
    {
        InterfaceBarViewModel bar = Ready();

        bar.ApplyPreflight(new PreflightResult(
            Preflights.Port(
                PortConflictSeverity.Serious,
                "UDP/67 is already bound on 0.0.0.0 by vmnetdhcp (pid 4312) on 0.0.0.0.",
                "Stop vmnetdhcp (pid 4312) before starting the server."),
            Preflights.Firewall(FirewallVerdict.Allowed, "Inbound UDP/67 is allowed."),
            Events.At));

        Assert.Equal(ReadinessState.Blocked, bar.Overall);
        Assert.Contains("vmnetdhcp", bar.Headline, StringComparison.Ordinal);
    }

    /// <summary>
    /// Hyper-V's Default Switch holds UDP/67 on a stock Windows machine. Reporting that the same
    /// way as a wildcard bind is exactly what trains people to ignore the warning.
    /// </summary>
    [Fact]
    public void AnAddressSpecificPortOwnerIsOnlyAWarning()
    {
        InterfaceBarViewModel bar = Ready();

        bar.ApplyPreflight(new PreflightResult(
            Preflights.Port(PortConflictSeverity.Advisory, "UDP/67 is bound by vmms (pid 900) on 172.28.0.1."),
            Preflights.Firewall(FirewallVerdict.Allowed, "Inbound UDP/67 is allowed."),
            Events.At));

        Assert.Equal(ReadinessState.Warning, bar.Overall);
    }

    [Fact]
    public void ADownAdapterBlocksAndCannotArmServeMode()
    {
        InterfaceBarViewModel bar = Ready();
        bar.ApplyInventory([Nics.Down()]);

        Assert.Equal(ReadinessState.Blocked, bar.Overall);
        Assert.False(bar.CanArmServeMode);
        Assert.Contains("link", bar.Headline, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// An APIPA address means the adapter asked for a lease and never got one. Requests still
    /// arrive, so this is not a blocker for watching - but no reply can be sourced from
    /// 169.254.x.x, so serving must stay unavailable.
    /// </summary>
    [Fact]
    public void AnApipaAddressWarnsAndCannotArmServeMode()
    {
        InterfaceBarViewModel bar = Ready();
        bar.ApplyInventory([Nics.Apipa()]);

        Assert.Equal(ReadinessState.Warning, bar.Overall);
        Assert.False(bar.CanArmServeMode);
    }

    [Fact]
    public void AFatalListenerClearsTheGreenLight()
    {
        InterfaceBarViewModel bar = Ready();
        Assert.Equal(ReadinessState.Ready, bar.Overall);

        bar.ApplyListener(ServerRunState.Faulted, DhcpServerMode.Serve, "The listening socket failed: NetworkDown.");

        Assert.Equal(ReadinessState.Blocked, bar.Overall);
        Assert.Contains("NetworkDown", bar.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public void AStoppedListenerIsGreyRatherThanGreen()
    {
        InterfaceBarViewModel bar = Ready();
        bar.ApplyListener(ServerRunState.Stopped, DhcpServerMode.Watch, null);

        Assert.Equal(ReadinessState.Unknown, bar.Overall);
    }

    /// <summary>
    /// Unplug and replug the cable mid-session: the bar tracks it, and the selection survives.
    /// Windows removes and re-adds USB adapters around exactly this, and silently dropping the
    /// user's choice would leave the tool listening to nothing and saying nothing about it.
    /// </summary>
    [Fact]
    public void KeepsTheSelectionThroughAnAdapterDisappearingAndComingBack()
    {
        InterfaceBarViewModel bar = Ready();
        AdapterOption? chosen = bar.SelectedAdapter;
        Assert.NotNull(chosen);

        bar.ApplyInventory([]);

        Assert.Same(chosen, bar.SelectedAdapter);
        Assert.False(chosen.IsPresent);
        Assert.Equal(ReadinessState.Blocked, bar.Overall);
        Assert.Contains(chosen, bar.Adapters);

        bar.ApplyInventory([Nics.Healthy()]);

        Assert.Same(chosen, bar.SelectedAdapter);
        Assert.True(chosen.IsPresent);
        Assert.Equal(ReadinessState.Ready, bar.Overall);
    }

    [Fact]
    public void DropsAnAdapterThatWentAwayAndWasNotChosen()
    {
        InterfaceBarViewModel bar = Bar();
        bar.ApplyInventory([Nics.Healthy(index: 12), Nics.Healthy(index: 20, address: "10.0.0.5")]);
        Assert.Equal(2, bar.Adapters.Count);

        bar.ApplyInventory([Nics.Healthy(index: 12)]);

        Assert.Single(bar.Adapters);
        Assert.Equal(12, bar.Adapters[0].Index);
    }

    [Fact]
    public void PutsPhysicalAdaptersAboveVirtualOnes()
    {
        InterfaceBarViewModel bar = Bar();

        NicInfo virtualNic = Nics.Healthy(index: 4, address: "172.28.0.1") with
        {
            Name = "vEthernet (Default Switch)",
            Description = "Hyper-V Virtual Ethernet Adapter",
        };

        bar.ApplyInventory([virtualNic, Nics.Healthy(index: 12)]);

        Assert.Equal(12, bar.Adapters[0].Index);
        Assert.Equal(4, bar.Adapters[1].Index);
    }

    /// <summary>
    /// "Start with the wrong NIC selected" - the bar says so, in the sentence the user needs,
    /// as soon as a request lands anywhere else.
    /// </summary>
    [Fact]
    public void NamesTheAdapterWhenRequestsArriveSomewhereElse()
    {
        InterfaceBarViewModel bar = Ready();

        bar.NoteArrival(4, "vEthernet (Default Switch)");

        Assert.NotNull(bar.MisdirectedTraffic);
        Assert.Contains("vEthernet (Default Switch)", bar.MisdirectedTraffic, StringComparison.Ordinal);

        bar.NoteArrival(12, "Ethernet");

        Assert.Null(bar.MisdirectedTraffic);
    }

    [Fact]
    public void ANeverRefreshedOuiTableReadsAsStale()
    {
        InterfaceBarViewModel bar = Bar();

        // OuiDatabase.Empty carries the Unix epoch, which is the same tell a clone that has never
        // run the packer gets: a 1970 date next to a screen full of unknown vendors.
        Assert.True(bar.OuiIsStale);
        Assert.Contains("1970-01-01", bar.OuiSummary, StringComparison.Ordinal);
    }

    private static InterfaceBarViewModel Bar() =>
        new(OuiDatabase.Empty, new TestTimeProvider(Events.At));

    /// <summary>A bar in the state where everything measurable has been measured and is good.</summary>
    private static InterfaceBarViewModel Ready()
    {
        InterfaceBarViewModel bar = Bar();

        bar.ApplyInventory([Nics.Healthy()]);
        bar.SelectedAdapter = bar.Adapters[0];
        bar.ApplyPreflight(Preflights.Clear());
        bar.ApplyListener(ServerRunState.Listening, DhcpServerMode.Watch, null);

        return bar;
    }
}
