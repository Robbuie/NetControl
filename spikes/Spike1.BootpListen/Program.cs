using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using NetControl.Spike.BootpListen;

// ---------------------------------------------------------------------------
// Spike 1 — BOOTP/DHCP listen + serve
//
// Purpose: prove two things before any real code gets written.
//   (a) We can receive BOOTP/DHCP requests and reliably identify which physical
//       NIC each one arrived on (via IP_PKTINFO), rather than guessing.
//   (b) We can find out whether UDP/67 is already owned by something else,
//       and say so by name instead of failing silently.
//
// It is also deliberately usable as a stopgap tool: --serve will actually
// hand out addresses.
// ---------------------------------------------------------------------------

Options opts;
try
{
    opts = CommandLine.Parse(args);
}
catch (Exception ex)
{
    Log.Error(ex.Message);
    Console.WriteLine();
    CommandLine.PrintUsage();
    return 1;
}

if (opts.ShowHelp) { CommandLine.PrintUsage(); return 0; }

Console.OutputEncoding = System.Text.Encoding.UTF8;
Log.Banner("BOOTP/DHCP spike");

// --- NIC inventory ---------------------------------------------------------
// Cached: NetworkInterface.GetAllNetworkInterfaces() is far too expensive to
// call per packet.
var nicCache = new NicCache();

Console.WriteLine("Interfaces:");
foreach (var n in nicCache.Snapshot()) Console.WriteLine("  " + n);
Console.WriteLine();

// --- Who else wants port 67? ----------------------------------------------
if (OperatingSystem.IsWindows()) PortOwner.Report(67);

if (opts.ListOnly) return 0;

// --- Which NIC do we care about? ------------------------------------------
NicInfo? bound = null;
if (opts.Nic is not null)
{
    bound = int.TryParse(opts.Nic, out int idx)
        ? nicCache.ByIndex(idx)
        : Nics.Find(opts.Nic);

    if (bound is null) { Log.Error($"No interface matched '{opts.Nic}'. Use --list."); return 2; }

    Log.Ok($"Filtering to [{bound.Index}] {bound.Name} ({bound.IPv4?.ToString() ?? "no IPv4"})");
    if (bound.IPv4 is null)
        Log.Warn("That NIC has no IPv4 address. Give it a static address in the device's subnet first.");
    else if (bound.IsApipa)
        Log.Warn("That NIC holds an APIPA (169.254.x.x) address — it never got a lease.");
    if (bound.Status != System.Net.NetworkInformation.OperationalStatus.Up)
        Log.Warn($"That NIC reports link status '{bound.Status}'. Check the cable.");
}
else Log.Ok("Listening on all interfaces (no --nic filter).");

// --- Mappings --------------------------------------------------------------
var mappings = opts.Mappings;
if (opts.CsvPath is not null)
{
    try
    {
        foreach (var (mac, a) in Csv.Load(opts.CsvPath)) mappings[mac] = a;
    }
    catch (Exception ex) { Log.Error($"CSV: {ex.Message}"); return 4; }
}

Console.WriteLine();
if (mappings.Count > 0)
{
    Console.WriteLine("Assignments loaded:");
    foreach (var (mac, a) in mappings)
        Console.WriteLine($"      {mac} -> {a.Ip}  mask {a.Mask}  gw {a.Gateway?.ToString() ?? "—"}");
    Log.Warn("SERVE MODE ACTIVE — this process will hand out addresses.");
}
else Log.Ok("Watch mode — logging only. Nothing will be sent.");

Console.WriteLine();
Console.WriteLine(new string('-', 104));

// --- Socket ----------------------------------------------------------------
using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
sock.EnableBroadcast = true;

// THE important line: ask the stack to report which interface each datagram
// arrived on. Without it you are guessing, and guessing is the root cause of
// the "bound to the wrong adapter" failure we are trying to eliminate.
sock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);

try
{
    sock.Bind(new IPEndPoint(IPAddress.Any, 67));
}
catch (SocketException ex)
{
    Log.Error($"Bind to 0.0.0.0:67 failed: {ex.SocketErrorCode} — {ex.Message}");
    if (ex.SocketErrorCode == SocketError.AccessDenied)
        Log.Error("AccessDenied means admin rights ARE required after all. Record that finding.");
    if (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        Log.Error("Another process holds UDP/67 exclusively. See the list above.");
    return 3;
}

bool elevated = OperatingSystem.IsWindows() && IsElevated();
Log.Ok($"Bound to 0.0.0.0:67   (process elevated: {elevated})");
if (!elevated)
    Log.Ok("Bind succeeded WITHOUT admin rights — Windows does not reserve low ports. Good news.");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
Console.WriteLine("Ctrl+C to stop.\n");

var buffer = new byte[2048];
var seen = new HashSet<string>();
int received = 0;

while (!cts.IsCancellationRequested)
{
    SocketReceiveMessageFromResult r;
    EndPoint any = new IPEndPoint(IPAddress.Any, 0);
    try
    {
        r = await sock.ReceiveMessageFromAsync(buffer, SocketFlags.None, any, cts.Token);
    }
    catch (OperationCanceledException) { break; }
    catch (SocketException ex) { Log.Error($"recv: {ex.SocketErrorCode}"); continue; }

    received++;
    int arrivalIndex = r.PacketInformation.Interface;
    NicInfo? arrivalNic = nicCache.ByIndex(arrivalIndex);

    if (bound is not null && arrivalIndex != bound.Index)
    {
        if (opts.Verbose)
            Log.Dim($"ignored: arrived on [{arrivalIndex}] {arrivalNic?.Name ?? "?"} (filtered out by --nic)");
        continue;
    }

    if (!BootpPacket.TryParse(buffer.AsSpan(0, r.ReceivedBytes), out var pkt))
    {
        Log.Dim($"non-BOOTP datagram, {r.ReceivedBytes} bytes, from {r.RemoteEndPoint}");
        continue;
    }
    if (pkt.Op != 1) continue;   // BOOTREQUEST only; ignore our own replies

    string kind = pkt.MessageType is { } mt ? $"DHCP {mt}" : "BOOTP REQUEST";
    string nicLabel = arrivalNic is null ? $"if#{arrivalIndex}" : $"[{arrivalNic.Index}] {arrivalNic.Name}";

    Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {kind,-16} mac={pkt.MacString}  " +
                      $"xid=0x{pkt.Xid:X8}  via {nicLabel}  dst={r.PacketInformation.Address}");

    if (seen.Add(pkt.MacString))
    {
        var extra = new List<string> { $"oui={Oui.Lookup(pkt.Mac)}" };
        if (pkt.HostName is { Length: > 0 } h) extra.Add($"host='{h}'");
        if (pkt.VendorClass is { Length: > 0 } vc) extra.Add($"vendor-class='{vc}'");
        extra.Add($"bcast-flag={pkt.WantsBroadcast}");
        Console.WriteLine($"              new device: {string.Join("  ", extra)}");
    }

    // --- Reply ---------------------------------------------------------
    if (!mappings.TryGetValue(pkt.MacString, out var assign)) continue;

    if (arrivalNic is null || arrivalNic.IPv4 is null)
    {
        Log.Warn($"              cannot reply: {nicLabel} has no IPv4 address to source from");
        continue;
    }

    var reply = BuildReply(pkt, assign, arrivalNic.IPv4);
    if (reply is null) continue;   // RELEASE/DECLINE/INFORM — nothing to send

    try
    {
        SendOut(sock, reply, arrivalNic, opts.SendMode, opts.ReplyPort);
        string what = pkt.MessageType switch
        {
            DhcpMessageType.Discover => "DHCP OFFER",
            DhcpMessageType.Request => "DHCP ACK",
            null => "BOOTP REPLY",
            _ => "reply"
        };
        Log.Ok($"              -> {what} {assign.Ip} to {pkt.MacString} out {nicLabel}");
    }
    catch (SocketException ex)
    {
        Log.Error($"              send failed: {ex.SocketErrorCode} — {ex.Message}");
        if (opts.SendMode == SendMode.UnicastIf)
            Log.Error("              retry with --send-mode persocket");
    }
}

Console.WriteLine($"\nStopped. {received} datagram(s) received, {seen.Count} distinct device(s) seen.");
if (received == 0)
{
    Log.Warn("Nothing arrived at all. Check, in order:");
    Console.WriteLine("""
          1. Windows Firewall — inbound UDP/67 must be allowed for this executable.
          2. Link light on the NIC, and that --nic picked the adapter the cable is in.
          3. The device is actually in BOOTP/DHCP mode (many ship DHCP-enabled).
          4. No managed switch in between filtering DHCP (some do this by policy).
          5. Power-cycle the device — most only request on boot, and only for a while.
    """);
}
return 0;


// ===========================================================================
static BootpPacket? BuildReply(BootpPacket req, Assignment a, IPAddress serverIp)
{
    var mt = req.MessageType;
    if (mt is DhcpMessageType.Release or DhcpMessageType.Decline or DhcpMessageType.Inform)
        return null;

    var rep = new BootpPacket
    {
        Op = 2,                 // BOOTREPLY
        HType = req.HType,
        HLen = req.HLen,
        Xid = req.Xid,
        Flags = req.Flags,      // echo the broadcast flag back
        YiAddr = a.Ip,
        SiAddr = serverIp,
        GiAddr = req.GiAddr,
        ChAddr = req.ChAddr,
    };

    if (mt is not null)
    {
        // Full DHCP exchange: DISCOVER -> OFFER, REQUEST -> ACK.
        rep.SetOption(53, mt == DhcpMessageType.Discover
            ? (byte)DhcpMessageType.Offer
            : (byte)DhcpMessageType.Ack);
        rep.SetOption(54, serverIp);              // server identifier
        rep.SetOptionUInt32(51, 8 * 24 * 3600);   // lease time
    }

    rep.SetOption(1, a.Mask);                      // subnet mask
    if (a.Gateway is not null) rep.SetOption(3, a.Gateway);
    return rep;
}

static void SendOut(Socket sock, BootpPacket reply, NicInfo nic, SendMode mode, int replyPort)
{
    byte[] bytes = reply.Serialize();

    // The client has no IP yet, so the reply must be a limited broadcast.
    // Pinning that broadcast to one NIC is the open question this spike settles.
    var dest = new IPEndPoint(IPAddress.Broadcast, replyPort);

    if (mode == SendMode.UnicastIf)
    {
        // IP_UNICAST_IF (option 31). For IPv4 the value is the interface index
        // in NETWORK byte order — an easy detail to get wrong, and it fails
        // silently when you do.
        const SocketOptionName IP_UNICAST_IF = (SocketOptionName)31;
        int netOrderIndex = IPAddress.HostToNetworkOrder(nic.Index);
        sock.SetSocketOption(SocketOptionLevel.IP, IP_UNICAST_IF, netOrderIndex);
        sock.SendTo(bytes, dest);
    }
    else
    {
        // Fallback: a short-lived socket bound to that NIC's own address, so the
        // routing decision is unambiguous. A bind per reply is irrelevant at
        // commissioning rates.
        using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        s.EnableBroadcast = true;
        s.Bind(new IPEndPoint(nic.IPv4!, 67));
        s.SendTo(bytes, dest);
    }
}

[SupportedOSPlatform("windows")]
static bool IsElevated()
{
    try
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(id)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }
    catch { return false; }
}
