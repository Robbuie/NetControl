using System.Net;
using System.Text;

namespace NetControl.Core.Reachability;

/// <summary>
/// One <c>HEAD /</c>, HTTP/1.0, connection closed after. HEAD rather than GET because the page
/// itself is not wanted - the status line and the Server header are - and an embedded web server
/// rendering a diagnostics page is the most expensive thing on this list for the device.
/// </summary>
public sealed class HttpHandshake : IProtocolHandshake
{
    private static readonly byte[] EndOfHeaders = "\r\n\r\n"u8.ToArray();

    public string Protocol => "HTTP";

    public byte[] BuildRequest(IPAddress address, int port) =>
        Encoding.ASCII.GetBytes(
            $"HEAD / HTTP/1.0\r\nHost: {address}\r\nUser-Agent: NetControl\r\nConnection: close\r\n\r\n");

    public int? ExpectedLength(ReadOnlySpan<byte> received)
    {
        int end = received.IndexOf(EndOfHeaders);
        return end < 0 ? null : end + EndOfHeaders.Length;
    }

    public ProtocolVerdict Judge(ReadOnlySpan<byte> reply, IPAddress address)
    {
        string status = BannerHandshake.FirstLine(reply);

        if (!status.StartsWith("HTTP/", StringComparison.Ordinal))
        {
            return ProtocolVerdict.Other(Protocol, status.Length == 0 ? "no HTTP status line" : $"answered '{status}'");
        }

        string? server = Header(Encoding.ASCII.GetString(reply), "Server");
        return ProtocolVerdict.Yes(Protocol, server is null ? status : $"{status}, server '{server}'");
    }

    /// <summary>One header's value, case-insensitively, or null.</summary>
    internal static string? Header(string headers, string name)
    {
        foreach (string line in headers.Split('\n'))
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0 && line[..colon].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                string value = line[(colon + 1)..].Trim();
                return value.Length == 0 ? null : value;
            }
        }

        return null;
    }
}
