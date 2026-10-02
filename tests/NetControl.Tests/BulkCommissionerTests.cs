using System.Net;
using NetControl.Core.Commissioning;
using Xunit;
using SimQuirk = NetControl.DeviceSim.Quirk;

namespace NetControl.Tests;

/// <summary>
/// Set static over a list, against simulated adapters on loopback. The single-device sequence is
/// tested in <see cref="StaticIpCommissionerTests"/>; these are about the loop - that each device is
/// its own operation, that one failure does not stop the rest, and that a stop lands between devices.
/// </summary>
public class BulkCommissionerTests
{
    private static readonly IPAddress Mask = IPAddress.Parse("255.255.255.0");

    [Fact]
    public async Task CommissionsEveryDeviceOnTheList()
    {
        await using SimulatedAdapter first = await SimulatedAdapter.StartAsync();
        await using SimulatedAdapter second = await SimulatedAdapter.StartAsync();

        BulkCommissionResult result = await new BulkCommissioner(Commissioner())
            .RunAsync([Request(first), Request(second)]);

        Assert.True(result.AllVerified);
        Assert.Equal(2, result.Verified);
        Assert.True(first.IsStatic);
        Assert.True(second.IsStatic);
    }

    /// <summary>A device pinned by switches is that device's problem, and the next one is still done.</summary>
    [Fact]
    public async Task OneDeviceFailingDoesNotStopTheRest()
    {
        await using SimulatedAdapter pinned = await SimulatedAdapter.StartAsync(SimQuirk.HardwarePinnedAddress);
        await using SimulatedAdapter fine = await SimulatedAdapter.StartAsync();

        BulkCommissionResult result = await new BulkCommissioner(Commissioner())
            .RunAsync([Request(pinned), Request(fine)]);

        Assert.Equal(1, result.Verified);
        Assert.Equal(1, result.NotVerified);
        Assert.Equal(CommissionOutcome.NotSettable, result.Completed[0].Result.Outcome);
        Assert.True(fine.IsStatic);
        Assert.False(result.AllVerified);
    }

    /// <summary>
    /// A stop asked for while a device is in progress lets that device finish and starts no more.
    /// Half way through a device is the worst place to stop; between two is the only safe one.
    /// </summary>
    [Fact]
    public async Task AStopLandsBetweenDevicesAndSendsNothingToTheRest()
    {
        await using SimulatedAdapter first = await SimulatedAdapter.StartAsync();
        await using SimulatedAdapter second = await SimulatedAdapter.StartAsync();
        await using SimulatedAdapter third = await SimulatedAdapter.StartAsync();

        var bulk = new BulkCommissioner(Commissioner());
        bulk.DeviceStarting += (_, e) =>
        {
            if (e.Index == 0)
            {
                bulk.RequestStop();
            }
        };

        BulkCommissionResult result = await bulk.RunAsync([Request(first), Request(second), Request(third)]);

        Assert.True(result.WasStopped);
        Assert.Single(result.Completed);
        Assert.Equal(2, result.NotStarted.Count);
        Assert.True(first.IsStatic);
        Assert.True(second.IsStillDynamic);
        Assert.True(third.IsStillDynamic);
        Assert.Contains("nothing was sent to them", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RaisesStartAndFinishForEachDeviceInOrder()
    {
        await using SimulatedAdapter first = await SimulatedAdapter.StartAsync();
        await using SimulatedAdapter second = await SimulatedAdapter.StartAsync();

        var bulk = new BulkCommissioner(Commissioner());
        var events = new List<string>();
        bulk.DeviceStarting += (_, e) => events.Add($"start {e.Index}");
        bulk.DeviceFinished += (_, e) => events.Add($"finish {e.Index} {e.Result?.Outcome}");

        await bulk.RunAsync([Request(first), Request(second)]);

        Assert.Equal(
            new[] { "start 0", "finish 0 Verified", "start 1", "finish 1 Verified" },
            events);
    }

    private static StaticIpCommissioner Commissioner() => new()
    {
        ConnectTimeout = TimeSpan.FromSeconds(2),
        VerifyAttempts = 5,
        VerifyDelay = TimeSpan.FromMilliseconds(20),
    };

    private static StaticIpRequest Request(SimulatedAdapter adapter) => new()
    {
        DeviceAddress = IPAddress.Loopback,
        Ip = IPAddress.Loopback,
        Mask = Mask,
        Port = adapter.Port,
    };
}
