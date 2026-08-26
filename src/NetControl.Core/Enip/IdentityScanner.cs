using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using NetControl.Core.Dhcp;
using NetControl.Core.Interfaces;

namespace NetControl.Core.Enip;

/// <summary>
/// Finds devices that already have an address, by asking them who they are.
///
/// <para>ListIdentity is the one EtherNet/IP operation that needs no TCP session, no CIP routing
/// and no session handle: a 24-byte encapsulation header with command 0x0063 and nothing after it,
/// over UDP. Every device on the segment that speaks EtherNet/IP answers with its identity.</para>
///
/// <para><b>This is the opposite half of the tool from the BOOTP log.</b> The log finds devices
/// asking for an address; this finds devices that already have one. A device that answers here
/// does not need BOOTP - so the value of a scan is verification (did the device we just served
/// come up where we told it to), conflict detection (something is already sitting on the address
/// we planned), and inventory (what is on this segment at all).</para>
///
/// <para><b>Safety.</b> A scan transmits, so it obeys the same discipline as serve mode. It sends
/// ListIdentity and nothing else - no forward-open, no reset, no attribute write as a side effect
/// of looking. It is only ever called from something the user pressed. It is bounded: one
/// broadcast, plus whatever unicast addresses the caller handed it, paced. And a scan with nothing
/// to probe transmits nothing at all rather than defaulting to a broadcast.</para>
///
/// <para>All framing lives in non-async static helpers, because <c>Span&lt;T&gt;</c> locals are
/// illegal in an async method (CS4013).</para>
/// </summary>
public sealed class IdentityScanner : IIdentityScanner
{
    /// <summary>IP_UNICAST_IF. Not in <see cref="SocketOptionName"/>, so it is spelled out here.</summary>
    private const SocketOptionName IpUnicastIf = (SocketOptionName)31;

    private const int HeaderLength = 24;

    /// <summary>
    /// A reply is about sixty bytes plus the product name. One kilobyte is room for a name far
    /// longer than a SHORT_STRING can hold, so a truncated read is not a failure mode here.
    /// </summary>
    private const int ReceiveBufferSize = 1024;

    private readonly ScanOptions _options;

    public IdentityScanner(ScanOptions? options = null) => _options = options ?? new ScanOptions();

    public async Task<ScanReport> ScanAsync(
        NicInfo nic,
        IReadOnlyList<IPAddress>? unicastTargets = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nic);

        if (!nic.CanServe)
        {
            throw new EnipException(
                $"[{nic.Index}] {nic.Name} cannot source a scan: "
                + (nic.IsUp ? $"it has {(nic.IsApipa ? "only an APIPA address" : "no IPv4 address")}." : "it is down."))
            {
                Remediation = "Give the adapter a static address on the target subnet, or select a different one.",
            };
        }

        IReadOnlyList<IPAddress> targets = CheckTargets(nic, unicastTargets);

        // Nothing to send is not the same as "send a broadcast anyway". A scan the user narrowed
        // down to nothing must put nothing on the wire.
        if (!_options.Broadcast && targets.Count == 0)
        {
            return new ScanReport(nic, [], 0, 0, 0, 0, TimeSpan.Zero);
        }

        var clock = Stopwatch.StartNew();
        using Socket socket = Open(nic);

        byte[] probe = BuildProbe();
        int sent = 0;
        int refused = 0;

        if (_options.Broadcast)
        {
            sent += await ProbeAsync(socket, probe, IPAddress.Broadcast, cancellationToken).ConfigureAwait(false);
        }

        foreach (IPAddress target in targets)
        {
            if (_options.UnicastPace > TimeSpan.Zero && sent > 0)
            {
                await Task.Delay(_options.UnicastPace, cancellationToken).ConfigureAwait(false);
            }

            sent += await ProbeAsync(socket, probe, target, cancellationToken).ConfigureAwait(false);
        }

        var found = new List<ListIdentityReply>();
        var seen = new HashSet<DeviceKey>();
        var buffer = new byte[ReceiveBufferSize];
        int duplicates = 0;
        int ignored = 0;

        // The window starts once the last probe is away, not when the scan began: a slow sweep
        // would otherwise spend its listening time still sending.
        DateTimeOffset deadline = DateTimeOffset.UtcNow + _options.Window;

        while (true)
        {
            TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            window.CancelAfter(remaining);

            SocketReceiveFromResult received;

            try
            {
                received = await socket
                    .ReceiveFromAsync(buffer, SocketFlags.None, AnyEndPoint(), window.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break;      // the window closed, which is how a scan ends
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                // A UDP socket on Windows surfaces an ICMP port-unreachable as a receive error on
                // the *next* read, not on the send. It means a host is at that address and is not
                // listening on 44818 - which is worth counting rather than discarding, because a
                // switch, an HMI or a PC sitting on a planned address is exactly what a scan is for.
                refused++;
                continue;
            }
            catch (SocketException)
            {
                ignored++;
                continue;
            }

            if (received.RemoteEndPoint is not IPEndPoint from
                || !ListIdentityReply.TryParse(
                    buffer.AsSpan(0, received.ReceivedBytes), from, out ListIdentityReply? reply)
                || reply is null)
            {
                ignored++;
                continue;
            }

            // Deduped by identity, not by address. Collapsing on address alone would hide the one
            // thing a scan is uniquely able to catch: two different devices answering on the same
            // address. A device that answers both the broadcast and a unicast probe still collapses,
            // because its serial number and its endpoint are the same both times.
            if (!seen.Add(DeviceKey.For(reply)))
            {
                duplicates++;
                continue;
            }

            found.Add(reply);
        }

        clock.Stop();
        return new ScanReport(nic, found, sent, duplicates, ignored, refused, clock.Elapsed);
    }

    /// <summary>
    /// Refuses anything in the target list that is not one host. A sweep is a list of unicast
    /// probes; the moment a broadcast address gets into it, "gentle and bounded" stops being true
    /// and every device on the segment answers at once.
    /// </summary>
    private static IReadOnlyList<IPAddress> CheckTargets(NicInfo nic, IReadOnlyList<IPAddress>? targets)
    {
        if (targets is null || targets.Count == 0)
        {
            return [];
        }

        bool knowsTheSubnet = Ipv4Subnet.TryCreate(nic.IPv4, nic.Mask, out Ipv4Subnet subnet);

        foreach (IPAddress target in targets)
        {
            ArgumentNullException.ThrowIfNull(target);

            if (target.AddressFamily != AddressFamily.InterNetwork)
            {
                throw new EnipException($"'{target}' is not an IPv4 address, and EtherNet/IP discovery is IPv4.");
            }

            bool isDirectedBroadcast = knowsTheSubnet && target.Equals(subnet.Broadcast);

            if (target.Equals(IPAddress.Broadcast) || isDirectedBroadcast || target.GetAddressBytes()[0] >= 224)
            {
                throw new EnipException($"'{target}' is a broadcast or multicast address, not one device.")
                {
                    Remediation = "A unicast sweep probes hosts one at a time. Use the broadcast option if "
                        + "that is what you meant - it is one datagram rather than one per address.",
                };
            }
        }

        return targets;
    }

    private static EndPoint AnyEndPoint() => new IPEndPoint(IPAddress.Any, 0);

    /// <summary>
    /// The whole request: an encapsulation header with command 0x0063 and no data at all.
    ///
    /// <para>The sender context is random per scan. Nothing filters on it - some devices echo it
    /// and some zero it, so filtering would discard real replies - but it is what makes a
    /// bench-session pcap readable, since it ties every reply back to the probe that caused it.</para>
    /// </summary>
    private static byte[] BuildProbe()
    {
        var frame = new byte[HeaderLength];
        BinaryPrimitives.WriteUInt16LittleEndian(frame, ListIdentityReply.Command);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2), 0);       // no data
        RandomNumberGenerator.Fill(frame.AsSpan(12, 8));                    // sender context
        return frame;
    }

    private Socket Open(NicInfo nic)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        try
        {
            socket.EnableBroadcast = _options.Broadcast;
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

            if (_options.SendMode == ReplySendMode.UnicastInterfaceOption)
            {
                socket.Bind(new IPEndPoint(IPAddress.Any, 0));

                // IP_UNICAST_IF takes the interface index in NETWORK byte order for IPv4. Host
                // order fails silently and the datagram leaves by whichever adapter Windows would
                // have picked - which on a laptop with a VPN up is not the one in your hand.
                socket.SetSocketOption(SocketOptionLevel.IP, IpUnicastIf, IPAddress.HostToNetworkOrder(nic.Index));
            }
            else
            {
                socket.Bind(new IPEndPoint(nic.IPv4!, 0));
            }
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            throw new EnipException(
                $"Cannot open a discovery socket on [{nic.Index}] {nic.Name} ({nic.IPv4}): {ex.SocketErrorCode}.", ex)
            {
                Remediation = "Check the adapter still holds that address - unplugging and replugging a cable "
                    + "can lose it - and that nothing else has the socket.",
            };
        }

        return socket;
    }

    private async Task<int> ProbeAsync(
        Socket socket, byte[] probe, IPAddress target, CancellationToken cancellationToken)
    {
        try
        {
            await socket
                .SendToAsync(probe, SocketFlags.None, new IPEndPoint(target, _options.Port), cancellationToken)
                .ConfigureAwait(false);

            return 1;
        }
        catch (SocketException)
        {
            // One unreachable address must not end a sweep: on a segment where half the planned
            // range is empty, that would mean the scan stops at the first gap.
            return 0;
        }
    }

    /// <summary>
    /// What makes two replies the same device: where it answered from, and what it says it is.
    /// The serial number alone is not enough - it is unique per vendor, not globally.
    /// </summary>
    private readonly record struct DeviceKey(
        IPAddress Sender, int Port, ushort VendorId, ushort ProductCode, uint Serial)
    {
        public static DeviceKey For(ListIdentityReply reply) =>
            new(reply.Sender.Address, reply.Sender.Port, reply.VendorId, reply.ProductCode, reply.SerialNumber);
    }
}
