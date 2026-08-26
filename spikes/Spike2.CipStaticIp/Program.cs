using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using NetControl.Spike.CipStaticIp;

// ---------------------------------------------------------------------------
// Spike 2 - set a static IP over EtherNet/IP, then prove it took.
//
// This replaces the Rockwell tool's "Disable BOOTP/DHCP" button. The difference
// that matters: we read the result back and re-discover the device before we
// claim success, instead of assuming the write worked.
// ---------------------------------------------------------------------------

if (args.Length == 0 || args[0] is "-h" or "--help" or "/?") { Usage(); return 0; }

try
{
    return args[0].ToLowerInvariant() switch
    {
        "discover" => await Discover(args[1..]),
        "read"     => await Read(args[1..]),
        "set"      => await Set(args[1..]),
        _          => Unknown(args[0])
    };
}
catch (Exception ex)
{
    Err(ex is EnipException or TimeoutException ? ex.Message : ex.ToString());
    return 1;
}


// ===========================================================================
static async Task<int> Discover(string[] args)
{
    IPAddress? local = args.Length > 0 ? IPAddress.Parse(args[0]) : null;

    Console.WriteLine($"ListIdentity broadcast on UDP {EnipSession.Port}" +
                      (local is null ? " (all interfaces)" : $" from {local}") + " ...\n");

    var devices = await EnipSession.DiscoverAsync(local);
    if (devices.Count == 0)
    {
        Warn("No EtherNet/IP devices answered.");
        Console.WriteLine("""
              Either nothing is on this subnet, the reply was blocked by Windows Firewall,
              or the broadcast went out the wrong adapter. Pass your NIC's own IP as an
              argument to pin the source interface:  cip-spike discover 192.168.1.10
        """);
        return 2;
    }

    Console.WriteLine($"{"Address",-16} {"Product",-34} {"Rev",-6} {"Serial",-12} Vendor");
    Console.WriteLine(new string('-', 100));
    foreach (var d in devices.OrderBy(d => d.IpAddress.ToString()))
        Console.WriteLine($"{d.IpAddress,-16} {Truncate(d.ProductName, 34),-34} " +
                          $"{d.MajorRevision}.{d.MinorRevision,-4} 0x{d.SerialNumber:X8}   {d.Vendor}");

    Console.WriteLine($"\n{devices.Count} device(s).");
    return 0;
}

// ---------------------------------------------------------------------------
static async Task<int> Read(string[] args)
{
    if (args.Length < 1) { Err("read needs a device address"); return 1; }
    var target = IPAddress.Parse(args[0]);

    Console.WriteLine($"Connecting to {target}:{EnipSession.Port} ...");
    using var session = await EnipSession.ConnectAsync(target);
    Ok($"Session registered, handle 0x{session.SessionHandle:X8}\n");

    await DumpDevice(session);
    return 0;
}

// ---------------------------------------------------------------------------
static async Task<int> Set(string[] args)
{
    if (args.Length < 1) { Err("set needs a device address"); return 1; }
    var target = IPAddress.Parse(args[0]);

    IPAddress? newIp = null, mask = IPAddress.Parse("255.255.255.0"), gw = null;
    bool reset = false, verify = true;

    for (int i = 1; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--ip":   newIp = IPAddress.Parse(args[++i]); break;
            case "--mask": mask  = IPAddress.Parse(args[++i]); break;
            case "--gw":   gw    = IPAddress.Parse(args[++i]); break;
            case "--reset": reset = true; break;
            case "--no-verify": verify = false; break;
            default: Err($"unknown option '{args[i]}'"); return 1;
        }
    }

    newIp ??= target;   // no --ip means "keep this address, just make it static"

    Console.WriteLine($"Connecting to {target}:{EnipSession.Port} ...");
    using var session = await EnipSession.ConnectAsync(target);
    Ok($"Session registered, handle 0x{session.SessionHandle:X8}\n");

    // --- Ask what the device will accept before trying anything -----------
    var capRsp = await session.SendUnconnectedAsync(
        CipRequest.GetAttr(CipClass.TcpIpInterface, 1, 2));
    if (!capRsp.IsSuccess) { Err($"Cannot read Configuration Capability: {capRsp.StatusText()}"); return 3; }
    if (capRsp.Data.Length < 4) { Err("Configuration Capability reply was too short to decode."); return 3; }

    var cap = (ConfigCapability)ReadU32(capRsp.Data);
    Console.WriteLine($"  Configuration Capability : {cap}");

    if (!cap.HasFlag(ConfigCapability.ConfigurationSettable))
    {
        Err("Device reports that its interface configuration is NOT settable.");
        Console.WriteLine("""
              Usually this means the address is fixed by hardware - rotary switches or
              DIP switches on the module. Set them to 0/999 (module dependent) to hand
              control back to software, then retry.
        """);
        return 4;
    }
    bool resetNeeded = cap.HasFlag(ConfigCapability.InterfaceResetNeeded);
    if (resetNeeded) Warn("Device reports a reset is required before changes take effect.");

    // --- Step 1: switch Configuration Control to static -------------------
    // Order matters. Several adapters reject the attribute 5 write while they
    // are still in BOOTP or DHCP mode.
    Console.WriteLine("\n  [1/3] Configuration Control (attr 3) -> Static");
    var ctrl = new byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(ctrl, (uint)ConfigMethod.Static);

    var r1 = await session.SendUnconnectedAsync(
        CipRequest.SetAttr(CipClass.TcpIpInterface, 1, 3, ctrl));
    if (!r1.IsSuccess) { Err($"        failed: {r1.StatusText()}"); return 5; }
    Ok("        accepted");

    // --- Step 2: write the addresses --------------------------------------
    Console.WriteLine($"  [2/3] Interface Configuration (attr 5) -> {newIp} / {mask} / {gw?.ToString() ?? "no gateway"}");
    var cfg = new InterfaceConfig(newIp, mask, gw ?? IPAddress.Any,
                                  IPAddress.Any, IPAddress.Any, "");

    var r2 = await session.SendUnconnectedAsync(
        CipRequest.SetAttr(CipClass.TcpIpInterface, 1, 5, cfg.Serialize()));
    if (!r2.IsSuccess) { Err($"        failed: {r2.StatusText()}"); return 6; }
    Ok("        accepted");

    // --- Step 3: read it back ---------------------------------------------
    // If the address changed, the old session is about to die with the old
    // address, so verification has to happen over a fresh connection.
    Console.WriteLine("  [3/3] Verifying");

    if (reset || resetNeeded)
    {
        Console.WriteLine("        issuing Identity reset ...");
        try { await session.SendUnconnectedAsync(new CipRequest { Service = CipService.Reset, Class = CipClass.Identity, Instance = 1 }); }
        catch { /* the device drops the connection mid-reset; that is expected */ }
    }

    if (!verify) { Warn("        --no-verify given; stopping here. Confirm manually."); return 0; }

    bool moved = !newIp.Equals(target);
    Console.WriteLine($"        waiting for the device at {newIp} ...");

    for (int attempt = 1; attempt <= 10; attempt++)
    {
        await Task.Delay(1500);
        try
        {
            using var check = await EnipSession.ConnectAsync(newIp, timeoutMs: 2000);
            var rb = await check.SendUnconnectedAsync(CipRequest.GetAttr(CipClass.TcpIpInterface, 1, 5));
            var ctl = await check.SendUnconnectedAsync(CipRequest.GetAttr(CipClass.TcpIpInterface, 1, 3));
            if (!rb.IsSuccess) { Warn($"        readback attempt {attempt}: {rb.StatusText()}"); continue; }

            var actual = InterfaceConfig.Parse(rb.Data);
            var method = (ConfigMethod)(ReadU32(ctl.Data) & 0xF);

            Console.WriteLine($"        device reports: {actual}");
            Console.WriteLine($"        config method : {method}");

            bool ipOk = actual.Ip.Equals(newIp);
            bool maskOk = actual.Mask.Equals(mask);
            bool methodOk = method == ConfigMethod.Static;

            if (ipOk && maskOk && methodOk)
            {
                Ok($"\n  VERIFIED - {newIp} is set statically and survives a re-read.");
                if (!moved) Console.WriteLine("  Note: address unchanged; only the method moved to static.");
                Console.WriteLine("  Power-cycle the device and re-run 'read' to confirm it persists.");
                return 0;
            }

            Err("\n  MISMATCH - the device did not take the configuration as written:");
            if (!ipOk) Console.WriteLine($"        ip:     wanted {newIp}, got {actual.Ip}");
            if (!maskOk) Console.WriteLine($"        mask:   wanted {mask}, got {actual.Mask}");
            if (!methodOk) Console.WriteLine($"        method: wanted Static, got {method}");
            return 7;
        }
        catch (Exception ex) when (ex is TimeoutException or EnipException or System.Net.Sockets.SocketException)
        {
            Console.WriteLine($"        attempt {attempt}/10: not answering yet ({ex.GetType().Name})");
        }
    }

    Err($"\n  UNVERIFIED - the writes were accepted but {newIp} never came back.");
    Console.WriteLine("""
          Most likely one of:
            - your PC has no route to the new subnet (add a second IP on that NIC)
            - the device needs a power cycle before the change takes effect
            - the change was accepted into volatile memory only
        """);
    return 8;
}

// ---------------------------------------------------------------------------
static async Task DumpDevice(EnipSession session)
{
    // Identity object
    var idn = await session.SendUnconnectedAsync(
        new CipRequest { Service = CipService.GetAttributeAll, Class = CipClass.Identity, Instance = 1 });
    if (idn.IsSuccess) Console.WriteLine(FormatIdentity(idn.Data));

    // Ethernet Link object - MAC address, so we can be certain which box this is
    var mac = await session.SendUnconnectedAsync(CipRequest.GetAttr(CipClass.EthernetLink, 1, 3));
    if (mac.IsSuccess && mac.Data.Length >= 6)
        Console.WriteLine($"    MAC          : {string.Join(':', mac.Data.Take(6).Select(b => b.ToString("X2")))}");
    else
        Console.WriteLine($"    MAC          : unavailable ({mac.StatusText()})");

    // TCP/IP Interface object
    Console.WriteLine("\n  TCP/IP Interface Object (class 0xF5)");
    foreach (var (attr, label) in new (ushort, string)[] { (1, "Status"), (2, "Capability"), (3, "Config Control") })
    {
        var r = await session.SendUnconnectedAsync(CipRequest.GetAttr(CipClass.TcpIpInterface, 1, attr));
        if (!r.IsSuccess || r.Data.Length < 4) { Console.WriteLine($"    attr {attr} {label,-15}: {r.StatusText()}"); continue; }
        uint v = BinaryPrimitives.ReadUInt32LittleEndian(r.Data);
        string extra = attr switch
        {
            2 => $"  ({(ConfigCapability)v})",
            3 => $"  ({(ConfigMethod)(v & 0xF)})",
            _ => ""
        };
        Console.WriteLine($"    attr {attr} {label,-15}: 0x{v:X8}{extra}");
    }

    var cfgR = await session.SendUnconnectedAsync(CipRequest.GetAttr(CipClass.TcpIpInterface, 1, 5));
    if (cfgR.IsSuccess)
        Console.WriteLine($"    attr 5 Interface Cfg  : {InterfaceConfig.Parse(cfgR.Data)}");
    else
        Console.WriteLine($"    attr 5 Interface Cfg  : {cfgR.StatusText()}");

    // Sanity: is it pingable?
    try
    {
        using var ping = new Ping();
        var reply = await ping.SendPingAsync(session.Address, 1000);
        Console.WriteLine($"\n  ICMP: {reply.Status}" + (reply.Status == IPStatus.Success ? $" ({reply.RoundtripTime} ms)" : ""));
    }
    catch { /* ICMP is frequently blocked; not worth failing over */ }
}


// ===========================================================================
// Kept non-async on purpose: Span<T> locals cannot be declared inside an async
// method (CS4013), so all span-based decoding lives in plain helpers.
static string FormatIdentity(byte[] data)
{
    if (data.Length < 15) return "  Identity\n    (reply too short to decode)";

    var d = data.AsSpan();
    ushort vendor  = BinaryPrimitives.ReadUInt16LittleEndian(d);
    ushort devType = BinaryPrimitives.ReadUInt16LittleEndian(d[2..]);
    ushort product = BinaryPrimitives.ReadUInt16LittleEndian(d[4..]);
    byte major = d[6], minor = d[7];
    uint serial = BinaryPrimitives.ReadUInt32LittleEndian(d[10..]);
    byte nameLen = d[14];
    string name = d.Length >= 15 + nameLen
        ? System.Text.Encoding.ASCII.GetString(d.Slice(15, nameLen))
        : "(name truncated)";

    return $"""
      Identity
        product      : {name}
        vendor/type  : {vendor} / {devType}   product code {product}
        revision     : {major}.{minor}
        serial       : 0x{serial:X8}
    """;
}

static uint ReadU32(byte[] data) =>
    data.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(data) : 0u;

static int Unknown(string cmd) { Err($"Unknown command '{cmd}'"); Usage(); return 1; }

static string Truncate(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "...";

static void Ok(string m)   { var c = Console.ForegroundColor; Console.ForegroundColor = ConsoleColor.Green;  Console.WriteLine(m); Console.ForegroundColor = c; }
static void Warn(string m) { var c = Console.ForegroundColor; Console.ForegroundColor = ConsoleColor.Yellow; Console.WriteLine(m); Console.ForegroundColor = c; }
static void Err(string m)  { var c = Console.ForegroundColor; Console.ForegroundColor = ConsoleColor.Red;    Console.WriteLine(m); Console.ForegroundColor = c; }

static void Usage() => Console.WriteLine("""
    cip-spike - EtherNet/IP static IP configuration

    USAGE
      cip-spike discover [localIp]
          ListIdentity broadcast. Lists every EtherNet/IP device that answers.
          Pass your NIC's own IP to pin which adapter the broadcast leaves by.

      cip-spike read <deviceIp>
          Dump Identity, MAC, and the whole TCP/IP Interface Object. Read-only.
          Run this first - it tells you whether the device will accept a write.

      cip-spike set <deviceIp> [--ip <new>] [--mask <m>] [--gw <g>]
                               [--reset] [--no-verify]
          Set Configuration Control to Static, write the addresses, read back
          and confirm. Omit --ip to keep the current address and only switch
          the method to static (the "Disable BOOTP/DHCP" equivalent).
          --reset issues an Identity reset afterwards.

    EXAMPLES
      cip-spike discover 192.168.1.10
      cip-spike read 192.168.1.51
      cip-spike set 192.168.1.51
      cip-spike set 192.168.1.51 --ip 10.10.20.51 --mask 255.255.255.0 --gw 10.10.20.1

    NOTE
      After changing to a different subnet, your PC needs an address on that
      subnet too or verification cannot reach the device.
    """);
