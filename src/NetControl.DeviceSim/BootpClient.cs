using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace NetControl.DeviceSim;

/// <summary>
/// The client half of BOOTP/DHCP — what a real adapter does on power-up. Gives
/// Spike 1 (and later the real server) something to answer without hardware.
///
/// Windows caveat: the DHCP Client service normally holds UDP/68, so binding it here
/// often fails. When it does we fall back to an ephemeral source port and simply do
/// not see the reply — the request still goes out, which is enough to exercise the
/// receive path. Use --client-port to pick a free port and see the full round trip.
/// </summary>
public sealed class BootpClient(SimulatedDevice device, Action<string> log)
{
    private const int ServerPort = 67;
    private const int ClientPort = 68;

    public async Task RunAsync(bool useDhcp, int intervalSeconds, int clientPort, CancellationToken ct)
    {
        using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        sock.EnableBroadcast = true;

        int boundPort = clientPort;
        try
        {
            sock.Bind(new IPEndPoint(IPAddress.Any, clientPort));
            log($"BOOTP client bound to UDP {clientPort}");
        }
        catch (SocketException ex)
        {
            log($"WARNING: cannot bind UDP {clientPort} ({ex.SocketErrorCode}).");
            if (clientPort == ClientPort)
                log("         The Windows DHCP Client service normally owns port 68. Requests will");
            log("         still be sent, but replies will not be seen. Try --client-port 6868.");
            sock.Bind(new IPEndPoint(IPAddress.Any, 0));
            boundPort = ((IPEndPoint)sock.LocalEndPoint!).Port;
        }

        uint xid = (uint)Random.Shared.Next(int.MinValue, int.MaxValue);
        var buf = new byte[2048];
        int attempt = 0;

        while (!ct.IsCancellationRequested)
        {
            attempt++;
            byte[] request = useDhcp
                ? BuildDhcpDiscover(device.MacAddress, xid, device.HostName)
                : BuildBootpRequest(device.MacAddress, xid);

            try
            {
                await sock.SendToAsync(request, new IPEndPoint(IPAddress.Broadcast, ServerPort), ct);
                log($"sent {(useDhcp ? "DHCPDISCOVER" : "BOOTREQUEST")} #{attempt} " +
                    $"mac={device.MacString} xid=0x{xid:X8} from :{boundPort}");
            }
            catch (SocketException ex) { log($"send failed: {ex.SocketErrorCode}"); }

            // Listen for a reply until the next retransmission is due.
            var deadline = DateTime.UtcNow.AddSeconds(intervalSeconds);
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                int remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (remaining <= 0) break;

                using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
                window.CancelAfter(remaining);

                SocketReceiveFromResult r;
                try { r = await sock.ReceiveFromAsync(buf, new IPEndPoint(IPAddress.Any, 0), window.Token); }
                catch (OperationCanceledException) { break; }
                catch (SocketException) { break; }

                if (TryReadReply(buf.AsSpan(0, r.ReceivedBytes), device.MacAddress, xid,
                                 out var offered, out var mask, out var gw, out string kind))
                {
                    log($"RECEIVED {kind}: ip={offered} mask={mask} gw={gw?.ToString() ?? "none"} from {r.RemoteEndPoint}");

                    device.Ip = offered;
                    device.Mask = mask;
                    if (gw is not null) device.Gateway = gw;
                    log($"device is now reachable at {offered} — try:  cip-spike read {offered}");
                    return;
                }
            }
        }
    }

    // =======================================================================
    // Framing — non-async static helpers (Span<T> locals are illegal in async, CS4013)
    // =======================================================================
    private static byte[] BuildBootpRequest(byte[] mac, uint xid)
    {
        var buf = NewRequest(mac, xid);
        // Classic BOOTP: magic cookie and an END option, nothing more.
        var s = buf.AsSpan(236);
        WriteCookie(s);
        s[4] = 255;
        return buf;
    }

    private static byte[] BuildDhcpDiscover(byte[] mac, uint xid, string hostName)
    {
        var buf = NewRequest(mac, xid);
        var s = buf.AsSpan(236);
        WriteCookie(s);

        int i = 4;
        s[i++] = 53; s[i++] = 1; s[i++] = 1;                 // message type = DISCOVER

        s[i++] = 61; s[i++] = 7; s[i++] = 1;                 // client identifier: type 1 + MAC
        mac.AsSpan(0, 6).CopyTo(s[i..]); i += 6;

        var host = System.Text.Encoding.ASCII.GetBytes(hostName);
        if (host.Length is > 0 and < 64)
        {
            s[i++] = 12; s[i++] = (byte)host.Length;
            host.CopyTo(s[i..]); i += host.Length;
        }

        var vendor = System.Text.Encoding.ASCII.GetBytes("SIM-ENBT");
        s[i++] = 60; s[i++] = (byte)vendor.Length;
        vendor.CopyTo(s[i..]); i += vendor.Length;

        s[i++] = 55; s[i++] = 3; s[i++] = 1; s[i++] = 3; s[i++] = 6;   // request mask, router, DNS
        s[i++] = 255;                                                   // end
        return buf;
    }

    private static byte[] NewRequest(byte[] mac, uint xid)
    {
        var buf = new byte[548];                 // 236 fixed + 4 cookie + 300 options + 8 slack
        var s = buf.AsSpan();
        s[0] = 1;                                // BOOTREQUEST
        s[1] = 1;                                // Ethernet
        s[2] = 6;                                // hardware address length
        s[3] = 0;                                // hops
        BinaryPrimitives.WriteUInt32BigEndian(s[4..], xid);
        BinaryPrimitives.WriteUInt16BigEndian(s[8..], 0);       // secs
        BinaryPrimitives.WriteUInt16BigEndian(s[10..], 0x8000); // ask for a broadcast reply
        mac.AsSpan(0, 6).CopyTo(s[28..]);        // chaddr
        return buf;
    }

    private static void WriteCookie(Span<byte> s)
    {
        s[0] = 0x63; s[1] = 0x82; s[2] = 0x53; s[3] = 0x63;
    }

    private static bool TryReadReply(
        ReadOnlySpan<byte> b, byte[] expectedMac, uint expectedXid,
        out IPAddress ip, out IPAddress mask, out IPAddress? gateway, out string kind)
    {
        ip = IPAddress.Any;
        mask = IPAddress.Parse("255.255.255.0");
        gateway = null;
        kind = "BOOTREPLY";

        if (b.Length < 236) return false;
        if (b[0] != 2) return false;                                        // must be BOOTREPLY
        if (BinaryPrimitives.ReadUInt32BigEndian(b[4..]) != expectedXid) return false;
        if (!b.Slice(28, 6).SequenceEqual(expectedMac.AsSpan(0, 6))) return false;

        ip = new IPAddress(b.Slice(16, 4));                                 // yiaddr
        if (ip.Equals(IPAddress.Any)) return false;

        var rest = b[236..];
        if (rest.Length < 5) return true;
        if (!(rest[0] == 0x63 && rest[1] == 0x82 && rest[2] == 0x53 && rest[3] == 0x63)) return true;

        int i = 4;
        while (i < rest.Length)
        {
            byte code = rest[i++];
            if (code == 0) continue;
            if (code == 255) break;
            if (i >= rest.Length) break;
            byte len = rest[i++];
            if (i + len > rest.Length) break;

            switch (code)
            {
                case 1 when len == 4: mask = new IPAddress(rest.Slice(i, 4)); break;
                case 3 when len >= 4: gateway = new IPAddress(rest.Slice(i, 4)); break;
                case 53 when len == 1:
                    kind = rest[i] switch { 2 => "DHCPOFFER", 5 => "DHCPACK", 6 => "DHCPNAK", _ => "DHCP reply" };
                    break;
            }
            i += len;
        }
        return kind != "DHCPNAK";
    }
}
