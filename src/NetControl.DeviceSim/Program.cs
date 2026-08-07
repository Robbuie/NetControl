using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using NetControl.DeviceSim;

// ---------------------------------------------------------------------------
// NetControl.DeviceSim — a fake EtherNet/IP adapter.
//
// Lets both spikes (and later the real tool) be developed and regression-tested
// without hardware. Quirks reproduce the ways real devices misbehave.
// ---------------------------------------------------------------------------

if (args.Contains("-h") || args.Contains("--help")) { Usage(); return 0; }
if (args.Contains("--list-quirks")) { ListQuirks(); return 0; }

// --- defaults --------------------------------------------------------------
var ip = IPAddress.Parse("192.168.1.51");
var mask = IPAddress.Parse("255.255.255.0");
IPAddress gateway = IPAddress.Any;
IPAddress? bind = null;
byte[] mac = [0x00, 0x00, 0xBC, 0x5E, 0x11, 0x01];
string productName = "SIM-ENBT/A Simulated Adapter";
var method = ConfigMethod.Bootp;
var quirks = Quirk.None;

bool emitBootp = false, emitDhcp = false, serveCip = true;
int interval = 5, clientPort = 68;

try
{
    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--ip":       ip = IPAddress.Parse(args[++i]); break;
            case "--mask":     mask = IPAddress.Parse(args[++i]); break;
            case "--gw":       gateway = IPAddress.Parse(args[++i]); break;
            case "--bind":     bind = IPAddress.Parse(args[++i]); break;
            case "--mac":      mac = ParseMac(args[++i]); break;
            case "--name":     productName = args[++i]; break;
            case "--method":   method = Enum.Parse<ConfigMethod>(args[++i], true); break;
            case "--quirk":    quirks |= Enum.Parse<Quirk>(args[++i], true); break;
            case "--bootp":    emitBootp = true; break;
            case "--dhcp":     emitDhcp = true; break;
            case "--interval": interval = int.Parse(args[++i]); break;
            case "--client-port": clientPort = int.Parse(args[++i]); break;
            case "--no-cip":   serveCip = false; break;
            case "--list-quirks": break;
            default: throw new ArgumentException($"unknown argument '{args[i]}'");
        }
    }
}
catch (IndexOutOfRangeException) { Console.Error.WriteLine("An option is missing its value."); return 1; }
catch (Exception ex) { Console.Error.WriteLine(ex.Message); Console.WriteLine(); Usage(); return 1; }

var device = new SimulatedDevice
{
    Ip = ip,
    Mask = mask,
    Gateway = gateway,
    MacAddress = mac,
    ProductName = productName,
    Method = method,
    Quirks = quirks,
};

Console.WriteLine();
Console.WriteLine("  NetControl.DeviceSim");
Console.WriteLine("  =============");
Console.WriteLine();
Console.WriteLine($"  {device}");
if (quirks != Quirk.None)
{
    Console.WriteLine();
    foreach (Quirk q in Enum.GetValues<Quirk>())
        if (q != Quirk.None && quirks.HasFlag(q))
            Console.WriteLine($"    quirk: {q,-28} {Describe(q)}");
}
// --- reachability sanity check ----------------------------------------------
// The simulator listens on the machine's real addresses; --ip is only the address
// it *reports* over CIP. On real hardware those are the same thing. Locally they
// are not, and the symptom is a bare connect timeout that looks like a spike bug.
if (serveCip)
{
    var localV4 = NetworkInterface.GetAllNetworkInterfaces()
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Select(a => a.Address)
        .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
        .ToList();

    bool reachable = bind is null
        ? localV4.Any(a => a.Equals(device.Ip))
        : bind.Equals(device.Ip);

    if (!reachable)
    {
        Console.WriteLine($"  NOTE: nothing on this machine owns {device.Ip}, so");
        Console.WriteLine($"        `cip-spike read {device.Ip}` will time out.");
        Console.WriteLine();
        Console.WriteLine("        The simulator answers on any local address. Either connect to one");
        Console.WriteLine("        of those, or restart with --ip set to one so the address it reports");
        Console.WriteLine("        matches the address you reach it on:");
        Console.WriteLine();
        Console.WriteLine("            devicesim --ip 127.0.0.1");
        Console.WriteLine();
        Console.WriteLine("        Reachable here: " +
            string.Join(", ", localV4.Where(a => !a.Equals(IPAddress.Loopback)).Take(4)) +
            (localV4.Count > 0 ? ", 127.0.0.1" : ""));
        Console.WriteLine();
    }
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var tasks = new List<Task>();

if (serveCip)
    tasks.Add(new EnipServer(device, Log, bind).RunAsync(cts.Token));

if (emitBootp || emitDhcp)
    tasks.Add(new BootpClient(device, Log).RunAsync(emitDhcp, interval, clientPort, cts.Token));

if (tasks.Count == 0)
{
    Console.Error.WriteLine("Nothing to do — --no-cip was given with no --bootp or --dhcp.");
    return 1;
}

Console.WriteLine("  Ctrl+C to stop.\n");

try { await Task.WhenAll(tasks); }
catch (OperationCanceledException) { }

Console.WriteLine("\nStopped.");
return 0;


// ===========================================================================
static void Log(string message) =>
    Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {message}");

static byte[] ParseMac(string raw)
{
    var hex = new string(raw.Where(Uri.IsHexDigit).ToArray());
    if (hex.Length != 12) throw new ArgumentException($"'{raw}' is not a 48-bit MAC address");
    return Enumerable.Range(0, 6).Select(k => Convert.ToByte(hex.Substring(k * 2, 2), 16)).ToArray();
}

static string Describe(Quirk q) => q switch
{
    Quirk.RejectConfigWhileDynamic  => "refuses attr 5 unless attr 3 is Static first",
    Quirk.HardwarePinnedAddress     => "address fixed by switches; every write refused",
    Quirk.RequiresResetToApply      => "writes are held pending until an Identity reset",
    Quirk.LiesAboutWriteSuccess     => "returns success but discards the write",
    Quirk.SlowResponses             => "adds latency to every response",
    Quirk.DropConnectionOnWrite     => "drops TCP partway through a set",
    Quirk.IgnoresBroadcastDiscovery => "answers unicast ListIdentity only",
    _ => ""
};

static void ListQuirks()
{
    Console.WriteLine("\nAvailable quirks (repeat --quirk to combine):\n");
    foreach (Quirk q in Enum.GetValues<Quirk>())
        if (q != Quirk.None)
            Console.WriteLine($"  {q,-28} {Describe(q)}");
    Console.WriteLine();
}

static void Usage() => Console.WriteLine("""
    devicesim — a fake EtherNet/IP adapter for hardware-free development

    USAGE
      devicesim [--ip <a>] [--mask <m>] [--gw <g>] [--mac <m>] [--name <s>]
                [--method static|bootp|dhcp] [--quirk <name>]...
                [--bootp | --dhcp] [--interval <s>] [--client-port <p>]
                [--bind <localIp>] [--no-cip] [--list-quirks]

    WHAT IT DOES
      By default it serves CIP on TCP 44818 and answers ListIdentity on UDP 44818,
      so `cip-spike discover` and `cip-spike read` have something to talk to.

      With --bootp or --dhcp it also acts like an adapter that just powered up:
      it broadcasts requests until something answers, then adopts the address it
      was given. That exercises the whole Spike 1 path end to end.

    OPTIONS
      --ip / --mask / --gw   Starting network configuration (default 192.168.1.51/24).
      --mac                  MAC address, any common format.
      --name                 Product name reported by the Identity object.
      --method               Initial Configuration Control value (default bootp).
      --quirk <name>         Add a misbehaviour. Repeatable. See --list-quirks.
      --bootp                Emit classic BOOTP requests (RFC 951).
      --dhcp                 Emit DHCPDISCOVER instead (RFC 2131).
      --interval <s>         Seconds between retransmissions (default 5).
      --client-port <p>      Source port for requests (default 68).
      --bind <localIp>       Bind the CIP server to one local address instead of all.
      --no-cip               Skip the CIP server; only emit BOOTP/DHCP.

    TYPICAL SESSIONS

      Exercise CIP only:
        devicesim
        cip-spike discover
        cip-spike read 192.168.1.51
        cip-spike set 192.168.1.51 --ip 192.168.1.60

      Exercise the BOOTP path end to end, in two terminals:
        bootp-spike --serve 00:00:BC:5E:11:01=192.168.1.77
        devicesim --bootp --client-port 6868

      Reproduce the classic "write accepted, config never took" failure:
        devicesim --quirk LiesAboutWriteSuccess
        cip-spike set 192.168.1.51 --ip 192.168.1.60     # must report MISMATCH

    WINDOWS NOTE
      The DHCP Client service usually owns UDP/68, so --bootp will fall back to an
      ephemeral port and will not see the reply. Pass --client-port 6868 for a clean
      local round trip.
    """);
