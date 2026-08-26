using System.Net;
using NetControl.Core;
using NetControl.Core.Cip;
using NetControl.Core.Commissioning;
using Xunit;
using SimQuirk = NetControl.DeviceSim.Quirk;

namespace NetControl.Tests;

/// <summary>
/// Setting a static address and turning BOOTP/DHCP off - the half of the job that BOOTP alone does
/// not do, run end to end against a simulated adapter in this process.
///
/// <para>Every test here drives the real client against an independently written server over a
/// real TCP socket on loopback, so the encapsulation framing, the EPATH encoding and the
/// little-endian CIP address encoding are all exercised rather than asserted about.</para>
/// </summary>
public class StaticIpCommissionerTests
{
    private static readonly IPAddress Mask = IPAddress.Parse("255.255.255.0");

    /// <summary>Short: these run against loopback, where a device that is coming back has come back.</summary>
    private static StaticIpCommissioner Commissioner() => new()
    {
        ConnectTimeout = TimeSpan.FromSeconds(2),
        VerifyAttempts = 5,
        VerifyDelay = TimeSpan.FromMilliseconds(20),
    };

    /// <summary>
    /// The ordinary job: a device that took an address over BOOTP is told to keep it. Verified only
    /// after reading both attributes back off the device.
    /// </summary>
    [Fact]
    public async Task PinsTheAddressAndTurnsBootpOff()
    {
        await using SimulatedAdapter adapter = await SimulatedAdapter.StartAsync();
        Assert.True(adapter.IsStillDynamic);

        CommissionResult result = await Commissioner().RunAsync(Request(adapter));

        Assert.Equal(CommissionOutcome.Verified, result.Outcome);
        Assert.Equal(ConfigMethod.Static, result.ReportedMethod);
        Assert.Equal(IPAddress.Loopback, result.Readback?.Ip);

        // And on the device itself, which is the claim that matters.
        Assert.True(adapter.IsStatic);
        Assert.Equal(IPAddress.Loopback, adapter.ConfiguredIp);
    }

    /// <summary>
    /// The failure the Rockwell tool does not catch. The device returns CIP success for the write
    /// and silently discards it; only reading it back afterwards notices. If this ever reports
    /// Verified, the verification logic has broken and the tool has started lying.
    /// </summary>
    [Fact]
    public async Task CatchesADeviceThatReportsSuccessAndDiscardsTheWrite()
    {
        await using SimulatedAdapter adapter = await SimulatedAdapter.StartAsync(SimQuirk.LiesAboutWriteSuccess);

        CommissionResult result = await Commissioner().RunAsync(Request(adapter));

        Assert.Equal(CommissionOutcome.Mismatch, result.Outcome);
        Assert.False(result.IsVerified);
        Assert.Contains("holding something else", result.Message, StringComparison.Ordinal);

        // It still holds its original address, which is exactly what the readback saw.
        Assert.Equal(IPAddress.Parse("192.168.1.51"), adapter.ConfiguredIp);
    }

    /// <summary>
    /// Capability is read first so that a device whose address is pinned by switches is told about
    /// rather than written to. The assertion that matters is the last one: nothing was sent.
    /// </summary>
    [Fact]
    public async Task RefusesADeviceWhoseAddressIsPinnedBySwitchesWithoutWritingAnything()
    {
        await using SimulatedAdapter adapter = await SimulatedAdapter.StartAsync(SimQuirk.HardwarePinnedAddress);

        CommissionResult result = await Commissioner().RunAsync(Request(adapter));

        Assert.Equal(CommissionOutcome.NotSettable, result.Outcome);
        Assert.False(result.WroteToDevice);
        Assert.Contains("switches", result.Remediation!, StringComparison.OrdinalIgnoreCase);

        Assert.True(adapter.IsStillDynamic);
        Assert.Equal(IPAddress.Parse("192.168.1.51"), adapter.ConfiguredIp);
    }

    /// <summary>
    /// Attribute 3 goes before attribute 5, and this is the device that proves why: it refuses the
    /// address write while still in a dynamic mode. Reversing the two steps would turn this into an
    /// object state conflict, which is a status that gives no hint the order was the problem.
    /// </summary>
    [Fact]
    public async Task WritesTheModeBeforeTheAddressSoADynamicDeviceStillAccepts()
    {
        await using SimulatedAdapter adapter = await SimulatedAdapter.StartAsync(SimQuirk.RejectConfigWhileDynamic);

        CommissionResult result = await Commissioner().RunAsync(Request(adapter));

        Assert.Equal(CommissionOutcome.Verified, result.Outcome);
        Assert.Equal(IPAddress.Loopback, adapter.ConfiguredIp);
    }

    /// <summary>
    /// A device that holds the configuration until it is reset has not been commissioned yet, and
    /// saying otherwise is how a panel comes back on the old address the next morning. Resetting a
    /// module is an outage on a running line, so it is never a side effect - the caller asks for it.
    /// </summary>
    [Fact]
    public async Task WillNotResetADeviceUnlessAllowedAndDoesNotClaimSuccess()
    {
        await using SimulatedAdapter adapter = await SimulatedAdapter.StartAsync(SimQuirk.RequiresResetToApply);

        CommissionResult result = await Commissioner().RunAsync(Request(adapter));

        Assert.Equal(CommissionOutcome.Unverified, result.Outcome);
        Assert.False(result.ResetTheDevice);
        Assert.True(result.WroteToDevice);
        Assert.Contains("reset", result.Message, StringComparison.OrdinalIgnoreCase);

        // Held pending: the device is still answering at its old address.
        Assert.Equal(IPAddress.Parse("192.168.1.51"), adapter.ConfiguredIp);
    }

    [Fact]
    public async Task ResetsWhenExplicitlyAllowedAndThenVerifies()
    {
        await using SimulatedAdapter adapter = await SimulatedAdapter.StartAsync(SimQuirk.RequiresResetToApply);

        CommissionResult result = await Commissioner()
            .RunAsync(Request(adapter) with { AllowReset = true });

        Assert.Equal(CommissionOutcome.Verified, result.Outcome);
        Assert.True(result.ResetTheDevice);
        Assert.Equal(IPAddress.Loopback, adapter.ConfiguredIp);
    }

    /// <summary>Handing a device back to a plant DHCP server. Only attribute 3 moves.</summary>
    [Fact]
    public async Task PutsADeviceBackOnDhcpWithoutTouchingItsAddresses()
    {
        await using SimulatedAdapter adapter = await SimulatedAdapter.StartAsync();
        IPAddress before = adapter.ConfiguredIp;

        CommissionResult result = await Commissioner()
            .RunAsync(Request(adapter) with { Method = ConfigMethod.Dhcp });

        Assert.Equal(CommissionOutcome.Verified, result.Outcome);
        Assert.Equal(ConfigMethod.Dhcp, result.ReportedMethod);
        Assert.Equal(before, adapter.ConfiguredIp);
    }

    /// <summary>
    /// Nothing reached a device, so nothing changed - and that is worth stating separately from
    /// "we wrote and do not know what happened".
    /// </summary>
    [Fact]
    public async Task ReportsADeviceThatIsNotThereAsUnreachableAndUntouched()
    {
        // Started and stopped, so the port is real and nothing is on it.
        SimulatedAdapter adapter = await SimulatedAdapter.StartAsync();
        int port = adapter.Port;
        await adapter.DisposeAsync();

        CommissionResult result = await Commissioner().RunAsync(new StaticIpRequest
        {
            DeviceAddress = IPAddress.Loopback,
            Ip = IPAddress.Loopback,
            Mask = Mask,
            Port = port,
        });

        Assert.Equal(CommissionOutcome.Unreachable, result.Outcome);
        Assert.False(result.WroteToDevice);
    }

    /// <summary>
    /// Configuration is a conversation with one device. There is no path through this type that
    /// broadcasts, and the check is at the door rather than somewhere a refactor could step past.
    /// </summary>
    [Theory]
    [InlineData("255.255.255.255")]
    [InlineData("0.0.0.0")]
    public async Task RefusesToConfigureAnythingThatIsNotOneDevice(string target)
    {
        CommissionResult result = await Commissioner().RunAsync(new StaticIpRequest
        {
            DeviceAddress = IPAddress.Parse(target),
            Ip = IPAddress.Loopback,
            Mask = Mask,
        });

        Assert.Equal(CommissionOutcome.Unreachable, result.Outcome);
        Assert.False(result.WroteToDevice);
        Assert.Contains("never broadcast", result.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The run is narrated because it touches live equipment and takes seconds. Every step it
    /// reports is a line the caller puts in the append-only event log.
    /// </summary>
    [Fact]
    public async Task NarratesEveryStepItTakes()
    {
        await using SimulatedAdapter adapter = await SimulatedAdapter.StartAsync();

        StaticIpCommissioner commissioner = Commissioner();
        List<CommissionStep> steps = [];
        commissioner.Progress += (_, e) => steps.Add(e.Step);

        await commissioner.RunAsync(Request(adapter));

        Assert.Contains(CommissionStep.Connect, steps);
        Assert.Contains(CommissionStep.ReadCapability, steps);
        Assert.Contains(CommissionStep.WriteConfigMethod, steps);
        Assert.Contains(CommissionStep.WriteConfiguration, steps);
        Assert.Contains(CommissionStep.Verify, steps);
        Assert.Contains(CommissionStep.Finished, steps);

        // Capability is read before anything is written, always.
        Assert.True(steps.IndexOf(CommissionStep.ReadCapability) < steps.IndexOf(CommissionStep.WriteConfigMethod));
        Assert.True(steps.IndexOf(CommissionStep.WriteConfigMethod) < steps.IndexOf(CommissionStep.WriteConfiguration));
    }

    /// <summary>A device pinned by switches is never sent a reset, an address, or anything else.</summary>
    [Fact]
    public async Task SendsNoWriteStepAtAllWhenTheDeviceIsNotSettable()
    {
        await using SimulatedAdapter adapter = await SimulatedAdapter.StartAsync(SimQuirk.HardwarePinnedAddress);

        StaticIpCommissioner commissioner = Commissioner();
        List<CommissionStep> steps = [];
        commissioner.Progress += (_, e) => steps.Add(e.Step);

        await commissioner.RunAsync(Request(adapter) with { AllowReset = true });

        Assert.DoesNotContain(CommissionStep.WriteConfigMethod, steps);
        Assert.DoesNotContain(CommissionStep.WriteConfiguration, steps);
        Assert.DoesNotContain(CommissionStep.Reset, steps);
    }

    /// <summary>
    /// The commissioner carries what is already known about a device. A quirk recorded from a bench
    /// session drives the sequence without the device having to admit to it over the wire.
    /// </summary>
    [Fact]
    public async Task HonoursARecordedResetQuirkEvenIfTheDeviceDoesNotAskForOne()
    {
        await using SimulatedAdapter adapter = await SimulatedAdapter.StartAsync();

        StaticIpCommissioner commissioner = Commissioner();
        List<CommissionStep> steps = [];
        commissioner.Progress += (_, e) => steps.Add(e.Step);

        CommissionResult result = await commissioner.RunAsync(Request(adapter) with
        {
            Quirks = DeviceQuirks.RequiresResetToApply,
            AllowReset = true,
        });

        Assert.Contains(CommissionStep.Reset, steps);
        Assert.Equal(CommissionOutcome.Verified, result.Outcome);
    }

    /// <summary>
    /// The common job is not moving a device but making the address it already has permanent, and
    /// the result says which of the two happened.
    /// </summary>
    [Fact]
    public void KnowsWhetherItIsMovingADeviceOrPinningIt()
    {
        StaticIpRequest pinning = new()
        {
            DeviceAddress = IPAddress.Parse("192.168.1.51"),
            Ip = IPAddress.Parse("192.168.1.51"),
            Mask = Mask,
        };

        Assert.False(pinning.MovesAddress);
        Assert.True((pinning with { Ip = IPAddress.Parse("192.168.1.60") }).MovesAddress);
    }

    private static StaticIpRequest Request(SimulatedAdapter adapter) => new()
    {
        DeviceAddress = SimulatedAdapter.Address,

        // Pins the device where the test can still reach it. A test that moved a device onto a real
        // subnet would be a test that puts traffic somewhere it has no business being.
        Ip = IPAddress.Loopback,
        Mask = Mask,
        Port = adapter.Port,
    };
}
