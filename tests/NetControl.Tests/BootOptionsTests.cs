using System.Net;
using System.Text;
using NetControl.Core;
using NetControl.Core.Dhcp;
using NetControl.Core.Persistence;
using Xunit;

namespace NetControl.Tests;

/// <summary>
/// Next server, boot file, TFTP server, domain and DNS. The header fields always; the DHCP options
/// only when asked for; and an empty set leaves the reply exactly as it was.
/// </summary>
public class BootOptionsTests
{
    private const uint Lease = 8 * 24 * 3600;

    private static readonly IPAddress ServerIp = IPAddress.Parse("192.168.1.10");

    private static DeviceAssignment Planned => new(
        MacAddress.Parse(Frames.DeviceMac),
        IPAddress.Parse("192.168.1.51"),
        IPAddress.Parse("255.255.255.0"),
        IPAddress.Parse("192.168.1.1"));

    private static BootOptions Full => Create("192.168.1.20", "tftp-01", "images/r30ib.img", "plant.local", "192.168.1.2, 192.168.1.3");

    [Fact]
    public void AnEmptySetChangesNothing()
    {
        BootpPacket request = Discover(requested: null);

        BootpPacket plain = DhcpServer.BuildReply(request, Planned, ServerIp, Lease)!;
        BootpPacket withNone = DhcpServer.BuildReply(request, Planned, ServerIp, Lease, BootOptions.None)!;

        Assert.Equal(plain.Serialize(), withNone.Serialize());
    }

    [Fact]
    public void TheHeaderFieldsCarryNextServerFileAndServerName()
    {
        BootpPacket reply = DhcpServer.BuildReply(Discover(requested: []), Planned, ServerIp, Lease, Full)!;

        Assert.Equal(IPAddress.Parse("192.168.1.20"), reply.ServerIp);
        Assert.Equal("images/r30ib.img", reply.BootFileName);
        Assert.Equal("tftp-01", reply.ServerHostName);

        // The server identifier stays this PC: the next server is where to boot from, not who answered.
        Assert.Equal(ServerIp, reply.GetAddressOption(DhcpOptionCode.ServerIdentifier));
    }

    /// <summary>A request list that does not ask for them gets none of the options - only the header fields.</summary>
    [Fact]
    public void AddsOnlyTheOptionsTheClientAskedFor()
    {
        BootpPacket reply = DhcpServer.BuildReply(
            Discover(requested: [DhcpOptionCode.SubnetMask, DhcpOptionCode.BootFileName]), Planned, ServerIp, Lease, Full)!;

        Assert.Equal("images/r30ib.img", Encoding.ASCII.GetString(reply.Options[DhcpOptionCode.BootFileName]));
        Assert.False(reply.Options.ContainsKey(DhcpOptionCode.TftpServerName));
        Assert.False(reply.Options.ContainsKey(DhcpOptionCode.DomainName));
        Assert.False(reply.Options.ContainsKey(DhcpOptionCode.DomainNameServer));
    }

    [Fact]
    public void AClientWithNoRequestListGetsThemAll()
    {
        BootpPacket reply = DhcpServer.BuildReply(Discover(requested: null), Planned, ServerIp, Lease, Full)!;

        Assert.Equal("tftp-01", reply.GetAsciiOption(DhcpOptionCode.TftpServerName));
        Assert.Equal("plant.local", reply.GetAsciiOption(DhcpOptionCode.DomainName));
        Assert.Equal(
            new byte[] { 192, 168, 1, 2, 192, 168, 1, 3 },
            reply.Options[DhcpOptionCode.DomainNameServer]);
    }

    /// <summary>Plain BOOTP: header fields, and the RFC 1497 extensions - never the DHCP-era 66 and 67.</summary>
    [Fact]
    public void PlainBootpGetsTheHeaderFieldsAndNoDhcpOptions()
    {
        Assert.True(BootpPacket.TryParse(Frames.Bytes(Frames.BootpRequest), out BootpPacket? request));

        BootpPacket reply = DhcpServer.BuildReply(request!, Planned, ServerIp, Lease, Full)!;

        Assert.Equal("images/r30ib.img", reply.BootFileName);
        Assert.False(reply.Options.ContainsKey(DhcpOptionCode.BootFileName));
        Assert.False(reply.Options.ContainsKey(DhcpOptionCode.TftpServerName));
        Assert.True(reply.Options.ContainsKey(DhcpOptionCode.DomainName));
        Assert.Null(reply.MessageType);
    }

    [Fact]
    public void ListsEveryProblemAtOnceAndBuildsNothing()
    {
        bool ok = BootOptions.TryCreate(
            "192.168.1", "tftp-01", new string('x', 128), "planté.local", "192.168.1.2, nope",
            out BootOptions options, out IReadOnlyList<string> problems);

        Assert.False(ok);
        Assert.Same(BootOptions.None, options);
        Assert.Equal(4, problems.Count);
        Assert.Contains(problems, p => p.StartsWith("Next server", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("Boot file", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("Domain name", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("'nope'", StringComparison.Ordinal));
    }

    [Fact]
    public void ABroadcastIsNotANextServer()
    {
        Assert.False(BootOptions.TryCreate("255.255.255.255", null, null, null, null, out _, out IReadOnlyList<string> problems));
        Assert.Single(problems);
    }

    [Fact]
    public void BlankFieldsAreUnset()
    {
        Assert.True(BootOptions.TryCreate(" ", "", null, "\t", "", out BootOptions options, out _));
        Assert.True(options.IsEmpty);
        Assert.Equal("none", options.Describe());
    }

    [Fact]
    public void RoundTripsThroughTheProjectFile()
    {
        using ProjectStore project = ProjectStore.CreateInMemory("Boot");

        BootOptionsSettings.Save(project.Settings, Full);
        BootOptions read = BootOptionsSettings.Load(project.Settings);

        Assert.Equal(Full.Describe(), read.Describe());

        BootOptionsSettings.Save(project.Settings, BootOptions.None);
        Assert.True(BootOptionsSettings.Load(project.Settings).IsEmpty);
        Assert.Empty(project.Settings.All());
    }

    /// <summary>A stored value that no longer parses is never served - and is named.</summary>
    [Fact]
    public void AStoredValueThatNoLongerParsesIsNotServed()
    {
        using ProjectStore project = ProjectStore.CreateInMemory("Boot");
        project.Settings.Set(BootOptionsSettings.NextServerKey, "10.0.5");

        BootOptions read = BootOptionsSettings.Load(project.Settings, out IReadOnlyList<string> problems);

        Assert.True(read.IsEmpty);
        Assert.Single(problems);
    }

    private static BootOptions Create(string? next, string? server, string? file, string? domain, string? dns)
    {
        Assert.True(BootOptions.TryCreate(next, server, file, domain, dns, out BootOptions options, out IReadOnlyList<string> problems),
            string.Join("; ", problems));
        return options;
    }

    private static BootpPacket Discover(byte[]? requested)
    {
        Assert.True(BootpPacket.TryParse(Frames.Bytes(Frames.DhcpDiscover), out BootpPacket? request));
        request!.Options.Remove(DhcpOptionCode.ParameterRequestList);

        if (requested is not null)
        {
            request.Options[DhcpOptionCode.ParameterRequestList] = requested;
        }

        return request;
    }
}
