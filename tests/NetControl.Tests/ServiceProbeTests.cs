using System.Net;
using System.Net.Sockets;
using NetControl.Core.Reachability;
using Xunit;

namespace NetControl.Tests;

/// <summary>
/// The service check, against listeners this test opens on loopback - the only address a test here
/// may connect to. An open port and a refused one are told apart, and that difference is the point:
/// a refusal proves something is there.
/// </summary>
public class ServiceProbeTests
{
    [Fact]
    public async Task TellsAnOpenPortFromARefusedOne()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            int open = ((IPEndPoint)listener.LocalEndpoint).Port;
            int closed = UnusedPort();

            var probe = new ServiceProbe
            {
                PauseBetween = TimeSpan.Zero,
                Ports = [new ServicePort(open, "Test open", "a listener"), new ServicePort(closed, "Test closed", "nothing")],
            };

            ServiceProbeResult result = await probe.RunAsync(IPAddress.Loopback);

            Assert.Equal(ServiceState.Open, result.Checks[0].State);
            Assert.Equal(ServiceState.Refused, result.Checks[1].State);
            Assert.True(result.HostAnswered);
            Assert.Contains("Test open", result.Summary, StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>A web page on 80 is offered as plain HTTP - the HTTPS one usually brings a certificate warning.</summary>
    [Fact]
    public void PrefersPlainHttpWhenBothAreOpen()
    {
        var result = new ServiceProbeResult(
            IPAddress.Parse("192.168.1.51"),
            [
                new ServiceCheck(new ServicePort(80, "HTTP", string.Empty), ServiceState.Open, TimeSpan.Zero),
                new ServiceCheck(new ServicePort(443, "HTTPS", string.Empty), ServiceState.Open, TimeSpan.Zero),
            ]);

        Assert.Equal(new Uri("http://192.168.1.51/"), result.WebPage);
    }

    /// <summary>
    /// Nothing answering on any port is not evidence of anything - a firewall that drops connections
    /// looks the same as an empty address - and the summary says so rather than calling it off.
    /// </summary>
    [Fact]
    public void DoesNotCallTotalSilenceEvidenceTheDeviceIsOff()
    {
        var result = new ServiceProbeResult(
            IPAddress.Parse("192.168.1.51"),
            [new ServiceCheck(new ServicePort(502, "Modbus/TCP", string.Empty), ServiceState.NoAnswer, TimeSpan.Zero)]);

        Assert.False(result.HostAnswered);
        Assert.Contains("not evidence", result.Summary, StringComparison.Ordinal);
        Assert.Null(result.WebPage);
    }

    [Fact]
    public async Task RefusesABroadcastBeforeConnectingToAnything()
    {
        await Assert.ThrowsAsync<ReachabilityException>(
            () => new ServiceProbe().RunAsync(IPAddress.Parse("192.168.1.255"), IPAddress.Parse("255.255.255.0")));
    }

    /// <summary>The default list is short on purpose. If it grows into a port scan, this is where it shows.</summary>
    [Fact]
    public void TheDefaultListIsAShortListAndNotAPortScan()
    {
        Assert.InRange(ServicePort.Industrial.Count, 1, 16);
        Assert.Contains(ServicePort.Industrial, p => p.Port == 44818);
        Assert.Equal(ServicePort.Industrial.Count, ServicePort.Industrial.Select(p => p.Port).Distinct().Count());
    }

    private static int UnusedPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
