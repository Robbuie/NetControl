using System.Net;

namespace NetControl.Spike.BootpListen;

public enum SendMode { UnicastIf, PerSocket }

public sealed record Assignment(IPAddress Ip, IPAddress Mask, IPAddress? Gateway, string? HostName = null);

public sealed class Options
{
    public bool ShowHelp;
    public bool ListOnly;
    public bool Verbose;
    public string? Nic;
    public string? CsvPath;
    public SendMode SendMode = SendMode.UnicastIf;

    /// <summary>Destination port for replies. Always 68 against real hardware; only
    /// changed to test against NetControl.DeviceSim on the same machine, where the Windows
    /// DHCP Client service already owns port 68.</summary>
    public int ReplyPort = 68;

    public Dictionary<string, Assignment> Mappings = new(StringComparer.OrdinalIgnoreCase);
}

public static class CommandLine
{
    public static Options Parse(string[] args)
    {
        var o = new Options();
        IPAddress mask = IPAddress.Parse("255.255.255.0");
        IPAddress? gw = null;
        var pending = new List<(string Mac, IPAddress Ip)>();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-h" or "--help" or "/?": o.ShowHelp = true; break;
                case "--list": o.ListOnly = true; break;
                case "-v" or "--verbose": o.Verbose = true; break;
                case "--nic": o.Nic = Next(args, ref i); break;
                case "--csv": o.CsvPath = Next(args, ref i); break;
                case "--mask": mask = IPAddress.Parse(Next(args, ref i)); break;
                case "--gw": gw = IPAddress.Parse(Next(args, ref i)); break;
                case "--reply-port": o.ReplyPort = int.Parse(Next(args, ref i)); break;
                case "--send-mode":
                    o.SendMode = Next(args, ref i).ToLowerInvariant() switch
                    {
                        "persocket" => SendMode.PerSocket,
                        _ => SendMode.UnicastIf
                    };
                    break;
                case "--serve":
                {
                    var spec = Next(args, ref i);
                    int eq = spec.IndexOf('=');
                    if (eq < 0) throw new ArgumentException($"--serve expects MAC=IP, got '{spec}'");
                    pending.Add((NormalizeMac(spec[..eq]), IPAddress.Parse(spec[(eq + 1)..])));
                    break;
                }
                default:
                    throw new ArgumentException($"Unknown argument '{args[i]}'");
            }
        }

        // --mask/--gw apply to every --serve regardless of ordering.
        foreach (var (m, ip) in pending) o.Mappings[m] = new Assignment(ip, mask, gw);
        return o;
    }

    private static string Next(string[] args, ref int i)
    {
        if (++i >= args.Length) throw new ArgumentException($"'{args[i - 1]}' needs a value");
        return args[i];
    }

    /// <summary>Accepts AA:BB:CC:DD:EE:FF, AA-BB-..., aabbccddeeff, or dotted. Returns AA:BB:CC:DD:EE:FF.</summary>
    public static string NormalizeMac(string raw)
    {
        var hex = new string(raw.Where(Uri.IsHexDigit).ToArray());
        if (hex.Length != 12) throw new ArgumentException($"'{raw}' is not a 48-bit MAC address");
        return string.Join(':', Enumerable.Range(0, 6).Select(k => hex.Substring(k * 2, 2).ToUpperInvariant()));
    }

    public static void PrintUsage() => Console.WriteLine("""
        bootp-spike — BOOTP/DHCP listener and minimal server

        USAGE
          bootp-spike [--list] [--nic <name|index>] [--serve MAC=IP ...]
                      [--mask <m>] [--gw <g>] [--csv <file>]
                      [--send-mode unicastif|persocket] [-v]

        MODES
          (default)             Watch only. Logs every BOOTP/DHCP request with the NIC
                                it actually arrived on. Sends nothing.
          --serve / --csv       Actually answers. Only MACs you list are served.

        OPTIONS
          --list                Show interfaces and who owns UDP/67, then exit.
          --nic <name|index>    Only consider packets arriving on this interface.
                                Accepts the interface index, its name, or part of
                                the adapter description e.g. --nic "I219".
          --serve MAC=IP        Repeatable. MAC in any common format.
          --mask <m>            Subnet mask for all served addresses (default 255.255.255.0).
          --gw <g>              Default gateway for all served addresses (optional).
          --csv <file>          Columns: mac,ip,mask,gateway,hostname (header row optional).
          --send-mode           How the broadcast reply is pinned to one NIC.
                                unicastif (default) uses IP_UNICAST_IF on the shared
                                socket; persocket binds a temporary socket to the NIC
                                address. If the device never sees the reply, try the other.
          --reply-port <p>      Destination port for replies (default 68). Only change
                                this to test against NetControl.DeviceSim on the same machine.
          -v                    Log packets filtered out by --nic too.

        EXAMPLES
          bootp-spike --list
          bootp-spike --nic "I219"
          bootp-spike --nic 12 --serve 00:00:BC:2E:69:6F=192.168.1.51 --mask 255.255.255.0
          bootp-spike --nic 12 --csv panel-A.csv

        NOTES
          Binding UDP/67 on Windows should NOT require administrator rights.
          Inbound UDP/67 must be allowed through Windows Firewall or nothing arrives.
        """);
}
