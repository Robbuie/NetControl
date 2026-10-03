using System.Net;
using System.Net.Sockets;
using System.Text;
using NetControl.Core.Reachability;
using Xunit;

namespace NetControl.Tests;

/// <summary>
/// The protocol-verified service check. Each handshake is judged against hand-built replies first -
/// what a real server sends, and what something else squatting on the port sends - and then the
/// whole probe is run over loopback against listeners that answer like the real thing.
/// </summary>
public class ProtocolHandshakeTests
{
    private static readonly IPAddress Device = IPAddress.Parse("192.168.1.51");

    [Fact]
    public void ModbusRequestIsReadDeviceIdentificationAndNothingElse()
    {
        byte[] request = new ModbusHandshake().BuildRequest(Device, 502);

        // MBAP: tid 4E43, protocol 0, length 5, unit FF; PDU 2B 0E 01 00.
        Assert.Equal(new byte[] { 0x4E, 0x43, 0, 0, 0, 5, 0xFF, 0x2B, 0x0E, 0x01, 0x00 }, request);
    }

    [Fact]
    public void ModbusIdentityIsReadOutOfTheObjects()
    {
        byte[] reply =
        [
            0x4E, 0x43, 0, 0, 0, 0, 0xFF,
            0x2B, 0x0E, 0x01, 0x01, 0x00, 0x00, 0x03,
            0x00, 0x07, .. "Schneid"u8,
            0x01, 0x06, .. "ATV320"u8,
            0x02, 0x04, .. "V3.4"u8,
        ];
        reply[5] = (byte)(reply.Length - 6);

        var handshake = new ModbusHandshake();
        Assert.Equal(reply.Length, handshake.ExpectedLength(reply));

        ProtocolVerdict verdict = handshake.Judge(reply, Device);
        Assert.True(verdict.Confirmed);
        Assert.Equal("Schneid ATV320 V3.4", verdict.Text);
    }

    /// <summary>"Illegal function" is something only a Modbus server can say.</summary>
    [Fact]
    public void AModbusExceptionStillConfirmsModbus()
    {
        byte[] reply = [0x4E, 0x43, 0, 0, 0, 3, 0xFF, 0xAB, 0x01];

        ProtocolVerdict verdict = new ModbusHandshake().Judge(reply, Device);

        Assert.True(verdict.Confirmed);
        Assert.Contains("illegal function", verdict.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnHttpServerOnTheModbusPortIsNotModbus()
    {
        ProtocolVerdict verdict = new ModbusHandshake().Judge("HTTP/1.1 400 Bad Request\r\n\r\n"u8, Device);

        Assert.False(verdict.Confirmed);
    }

    [Fact]
    public void S7ConnectionConfirmAndRefusalBothProveTheTransport()
    {
        var handshake = new IsoTsapHandshake();
        byte[] request = handshake.BuildRequest(Device, 102);
        Assert.Equal(22, request.Length);
        Assert.Equal(request.Length, request[3]);
        Assert.Equal(request.Length - 5, request[4]);

        byte[] confirm = [0x03, 0x00, 0x00, 0x16, 0x11, 0xD0, 0x00, 0x01, 0x00, 0x01, 0x00, 0xC0, 0x01, 0x0A, 0xC1, 0x02, 0x01, 0x00, 0xC2, 0x02, 0x01, 0x02];
        byte[] refuse = [0x03, 0x00, 0x00, 0x0B, 0x06, 0x80, 0x00, 0x01, 0x00, 0x01, 0x00];

        Assert.Equal(22, handshake.ExpectedLength(confirm));
        Assert.Contains("rack 0, slot 2", handshake.Judge(confirm, Device).Text, StringComparison.Ordinal);
        Assert.True(handshake.Judge(refuse, Device).Confirmed);
        Assert.Contains("refused", handshake.Judge(refuse, Device).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void MelsecReadsTheCpuModel()
    {
        var handshake = new MelsecHandshake();
        byte[] request = handshake.BuildRequest(Device, 5007);
        Assert.Equal(new byte[] { 0x01, 0x01 }, request[11..13]);   // command 0101, Read CPU model

        byte[] reply = [0xD0, 0x00, 0x00, 0xFF, 0xFF, 0x03, 0x00, 0x14, 0x00, 0x00, 0x00, .. "Q03UDVCPU       "u8, 0x66, 0x02];

        Assert.Equal(reply.Length, handshake.ExpectedLength(reply));
        ProtocolVerdict verdict = handshake.Judge(reply, Device);
        Assert.True(verdict.Confirmed);
        Assert.Equal("CPU Q03UDVCPU", verdict.Text);
    }

    [Fact]
    public void MelsecEndCodeIsReportedAndStillConfirms()
    {
        byte[] reply = [0xD0, 0x00, 0x00, 0xFF, 0xFF, 0x03, 0x00, 0x02, 0x00, 0x59, 0xC0];

        ProtocolVerdict verdict = new MelsecHandshake().Judge(reply, Device);

        Assert.True(verdict.Confirmed);
        Assert.Contains("C059", verdict.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void OpcUaHelloCarriesItsOwnLengthAndTheEndpoint()
    {
        byte[] hello = new OpcUaHandshake().BuildRequest(Device, 4840);

        Assert.Equal("HELF"u8.ToArray(), hello[..4]);
        Assert.Equal(hello.Length, BitConverter.ToInt32(hello, 4));
        Assert.Contains("opc.tcp://192.168.1.51:4840", Encoding.UTF8.GetString(hello), StringComparison.Ordinal);

        byte[] ack = [.. "ACKF"u8, 28, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        Assert.True(new OpcUaHandshake().Judge(ack, Device).Confirmed);
    }

    [Fact]
    public void HttpNamesTheServer()
    {
        byte[] reply = "HTTP/1.0 200 OK\r\nContent-Type: text/html\r\nServer: GoAhead-Webs\r\n\r\n"u8.ToArray();
        var handshake = new HttpHandshake();

        Assert.Equal(reply.Length, handshake.ExpectedLength(reply));
        Assert.Equal("HTTP/1.0 200 OK, server 'GoAhead-Webs'", handshake.Judge(reply, Device).Text);
    }

    [Fact]
    public void BannersAreReadAndNothingIsSent()
    {
        var ssh = new BannerHandshake(BannerKind.Ssh);
        var ftp = new BannerHandshake(BannerKind.Ftp);

        Assert.Empty(ssh.BuildRequest(Device, 22));
        Assert.Equal("SSH-2.0-Cisco-1.25", ssh.Judge("SSH-2.0-Cisco-1.25\r\n"u8, Device).Text);
        Assert.True(ftp.Judge("220 PowerFlex FTP ready\r\n"u8, Device).Confirmed);
        Assert.False(ssh.Judge("220 PowerFlex FTP ready\r\n"u8, Device).Confirmed);
    }

    /// <summary>The whole probe against the simulated adapter: port open, and proven to be EtherNet/IP.</summary>
    [Fact]
    public async Task VerifiesEtherNetIpAgainstTheSimulator()
    {
        await using SimulatedAdapter adapter = await SimulatedAdapter.StartAsync();

        var probe = new ServiceProbe
        {
            PauseBetween = TimeSpan.Zero,
            Ports = [new ServicePort(adapter.Port, "EtherNet/IP", "simulated")],
            Handshakes = new Dictionary<int, IProtocolHandshake> { [adapter.Port] = new EnipHandshake() },
        };

        ServiceProbeResult result = await probe.RunAsync(IPAddress.Loopback);

        ServiceCheck check = Assert.Single(result.Checks);
        Assert.True(check.IsVerified);
        Assert.Equal("verified", check.StateText);
        Assert.Contains("SIM-ENBT/A", check.DetailText, StringComparison.Ordinal);
    }

    /// <summary>Something that accepts and says nothing is open but not proven - and the summary says so.</summary>
    [Fact]
    public async Task ASilentListenerIsOpenButUnproven()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var probe = new ServiceProbe
            {
                PauseBetween = TimeSpan.Zero,
                ReplyTimeout = TimeSpan.FromMilliseconds(300),
                Ports = [new ServicePort(port, "Modbus/TCP", "test")],
                Handshakes = new Dictionary<int, IProtocolHandshake> { [port] = new ModbusHandshake() },
            };

            ServiceProbeResult result = await probe.RunAsync(IPAddress.Loopback);

            ServiceCheck check = Assert.Single(result.Checks);
            Assert.True(check.IsUnverified);
            Assert.Contains("did not answer", check.DetailText, StringComparison.Ordinal);
            Assert.Contains("Not proven", result.Summary, StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>With verification off, the 0.8.0 behaviour: connect, close, send nothing.</summary>
    [Fact]
    public async Task SendsNothingWhenVerificationIsOff()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var probe = new ServiceProbe
            {
                PauseBetween = TimeSpan.Zero,
                VerifyProtocols = false,
                Ports = [new ServicePort(port, "Modbus/TCP", "test")],
                Handshakes = new Dictionary<int, IProtocolHandshake> { [port] = new ModbusHandshake() },
            };

            Task<ServiceProbeResult> running = probe.RunAsync(IPAddress.Loopback);
            using TcpClient accepted = await listener.AcceptTcpClientAsync();
            ServiceProbeResult result = await running;

            var buffer = new byte[16];
            int read = await accepted.GetStream().ReadAsync(buffer);
            Assert.Equal(0, read);
            Assert.Null(Assert.Single(result.Checks).Protocol);
        }
        finally
        {
            listener.Stop();
        }
    }
}
