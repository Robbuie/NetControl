using System.Text;
using NetControl.Core.Tftp;

namespace NetControl.Tests;

/// <summary>
/// Hand-built TFTP datagrams.
///
/// <para>These are synthetic, exactly as <see cref="Frames"/>' BOOTP frames are: they match what
/// the RFC says a client should emit rather than what a FANUC controller does emit. <b>When a
/// controller is next doing an image backup, take a capture and add the real ones.</b> A real WRQ
/// is worth more than every frame in this file, because the one thing a synthetic frame cannot
/// contain is the surprise.</para>
/// </summary>
internal static class TftpFrames
{
    /// <summary>The filename a FANUC image backup is expected to ask for. Unconfirmed.</summary>
    public const string ImageFileName = "FROM00.IMG";

    /// <summary>Builds a request the way a client would, without going through our own encoder.</summary>
    public static byte[] Request(ushort opcode, string fileName, string mode, params string[] options)
    {
        var bytes = new List<byte>
        {
            (byte)(opcode >> 8),
            (byte)(opcode & 0xFF),
        };

        AppendNulTerminated(bytes, fileName);
        AppendNulTerminated(bytes, mode);

        foreach (string part in options)
        {
            AppendNulTerminated(bytes, part);
        }

        return [.. bytes];
    }

    /// <summary>A plain write request with no options - the shape an older controller sends.</summary>
    public static byte[] PlainWriteRequest(string fileName = ImageFileName) =>
        Request((ushort)TftpOpcode.WriteRequest, fileName, "octet");

    /// <summary>A write request offering the three options a well-behaved client offers.</summary>
    public static byte[] NegotiatingWriteRequest(string fileName = ImageFileName) =>
        Request(
            (ushort)TftpOpcode.WriteRequest,
            fileName,
            "octet",
            "blksize", "1468",
            "tsize", "134217728",
            "timeout", "5");

    private static void AppendNulTerminated(List<byte> bytes, string value)
    {
        bytes.AddRange(Encoding.Latin1.GetBytes(value));
        bytes.Add(0);
    }
}
