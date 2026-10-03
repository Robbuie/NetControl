using System.Net;
using System.Text;

namespace NetControl.Core.Reachability;

/// <summary>
/// FTP, SSH and Telnet: the server speaks first, so this sends nothing at all and reads the
/// greeting. The banner is often the most useful line in the whole check - an FTP server on a
/// drive names its firmware, and an SSH banner names the switch's operating system.
/// </summary>
public sealed class BannerHandshake(BannerKind kind) : IProtocolHandshake
{
    /// <summary>Banners are one line; anything longer is cut, since it is shown in a list row.</summary>
    private const int MaxShown = 100;

    private const byte TelnetIac = 0xFF;

    public BannerKind Kind { get; } = kind;

    public string Protocol => Kind switch
    {
        BannerKind.Ftp => "FTP",
        BannerKind.Ssh => "SSH",
        _ => "Telnet",
    };

    public byte[] BuildRequest(IPAddress address, int port) => [];

    public int? ExpectedLength(ReadOnlySpan<byte> received)
    {
        // Telnet option negotiation has no line ending; the first burst is the whole answer.
        if (Kind == BannerKind.Telnet && received.Length > 0 && received[0] == TelnetIac)
        {
            return received.Length;
        }

        int newline = received.IndexOf((byte)'\n');
        return newline < 0 ? null : newline + 1;
    }

    public ProtocolVerdict Judge(ReadOnlySpan<byte> reply, IPAddress address)
    {
        if (reply.IsEmpty)
        {
            return ProtocolVerdict.Other(Protocol, "connected, but no greeting was sent");
        }

        if (Kind == BannerKind.Telnet && reply[0] == TelnetIac)
        {
            return ProtocolVerdict.Yes(Protocol, "Telnet option negotiation - a console, unencrypted");
        }

        string line = FirstLine(reply);

        return Kind switch
        {
            BannerKind.Ssh when line.StartsWith("SSH-", StringComparison.Ordinal)
                => ProtocolVerdict.Yes(Protocol, line),
            BannerKind.Ftp when line.Length >= 3 && line[0] is '1' or '2' or '3' or '4' or '5'
                && char.IsAsciiDigit(line[1]) && char.IsAsciiDigit(line[2])
                => ProtocolVerdict.Yes(Protocol, line),
            BannerKind.Telnet when line.Length > 0
                => ProtocolVerdict.Yes(Protocol, $"text console: {line}"),
            _ => ProtocolVerdict.Other(Protocol, line.Length == 0 ? "sent bytes that are not a greeting" : $"greeted with '{line}'"),
        };
    }

    /// <summary>The first line, printable characters only, trimmed to fit a row.</summary>
    internal static string FirstLine(ReadOnlySpan<byte> reply)
    {
        int end = reply.IndexOfAny((byte)'\r', (byte)'\n');
        ReadOnlySpan<byte> first = end < 0 ? reply : reply[..end];

        var text = new StringBuilder(Math.Min(first.Length, MaxShown));
        foreach (byte b in first)
        {
            if (text.Length >= MaxShown)
            {
                break;
            }

            if (b is >= 0x20 and < 0x7F)
            {
                text.Append((char)b);
            }
        }

        return text.ToString().Trim();
    }
}
