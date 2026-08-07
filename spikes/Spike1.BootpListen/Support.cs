using System.Net;

namespace NetControl.Spike.BootpListen;

public static class Log
{
    private static void Write(ConsoleColor c, string prefix, string msg)
    {
        var old = Console.ForegroundColor;
        Console.ForegroundColor = c;
        Console.WriteLine(prefix + msg);
        Console.ForegroundColor = old;
    }

    public static void Ok(string msg) => Write(ConsoleColor.Green, "  ok   ", msg);
    public static void Warn(string msg) => Write(ConsoleColor.Yellow, "  warn ", msg);
    public static void Error(string msg) => Write(ConsoleColor.Red, "  ERR  ", msg);
    public static void Dim(string msg) => Write(ConsoleColor.DarkGray, "       ", msg);

    public static void Banner(string title)
    {
        Console.WriteLine();
        Console.WriteLine($"  {title}");
        Console.WriteLine($"  {new string('=', title.Length)}");
        Console.WriteLine();
    }
}

public static class Csv
{
    /// <summary>Columns: mac,ip,mask,gateway,hostname. Header row optional. # comments allowed.</summary>
    public static Dictionary<string, Assignment> Load(string path)
    {
        var result = new Dictionary<string, Assignment>(StringComparer.OrdinalIgnoreCase);
        foreach (var (line, lineNo) in File.ReadAllLines(path).Select((l, i) => (l, i + 1)))
        {
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith('#')) continue;

            var f = t.Split(',', StringSplitOptions.TrimEntries);
            if (f.Length < 2) throw new InvalidDataException($"{path}:{lineNo}: need at least mac,ip");
            if (f[0].Equals("mac", StringComparison.OrdinalIgnoreCase)) continue;   // header

            string mac = CommandLine.NormalizeMac(f[0]);
            var ip = IPAddress.Parse(f[1]);
            var mask = f.Length > 2 && f[2].Length > 0 ? IPAddress.Parse(f[2]) : IPAddress.Parse("255.255.255.0");
            IPAddress? gw = f.Length > 3 && f[3].Length > 0 ? IPAddress.Parse(f[3]) : null;
            string? host = f.Length > 4 && f[4].Length > 0 ? f[4] : null;

            result[mac] = new Assignment(ip, mask, gw, host);
        }
        return result;
    }
}

/// <summary>
/// Minimal OUI lookup. Enough to tell a Rockwell adapter from a laptop while the
/// full IEEE OUI database is not yet bundled — that lands in Phase 1.
/// </summary>
public static class Oui
{
    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["00:00:BC"] = "Rockwell Automation / Allen-Bradley",
        ["00:1D:9C"] = "Rockwell Automation",
        ["5C:88:16"] = "Rockwell Automation",
        ["E4:90:69"] = "Rockwell Automation",
        ["F4:54:33"] = "Rockwell Automation",
        ["00:0F:73"] = "Rockwell Automation",
        ["08:00:06"] = "Siemens",
        ["00:1B:1B"] = "Siemens",
        ["20:87:56"] = "Siemens",
        ["00:0E:8C"] = "Siemens",
        ["00:80:F4"] = "Telemecanique / Schneider",
        ["00:00:54"] = "Schneider Electric",
        ["00:20:4A"] = "Lantronix",
        ["00:90:E8"] = "Moxa",
        ["00:30:11"] = "HMS / Anybus",
        ["00:1C:06"] = "Turck",
        ["00:07:AF"] = "Red Lion",
        ["00:60:35"] = "Dalsa / Cognex",
        ["00:0D:E0"] = "Cognex",
        ["00:11:1A"] = "Beckhoff",
        ["00:01:05"] = "Beckhoff",
    };

    public static string Lookup(byte[] mac)
    {
        if (mac.Length < 3) return "unknown";
        string prefix = $"{mac[0]:X2}:{mac[1]:X2}:{mac[2]:X2}";
        return Known.TryGetValue(prefix, out var v) ? v : $"unknown ({prefix})";
    }
}
