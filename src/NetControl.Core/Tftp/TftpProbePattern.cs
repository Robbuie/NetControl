namespace NetControl.Core.Tftp;

/// <summary>
/// The probe's payload: a read-only stream of a fixed length whose every byte is a function of its
/// position, so 40 MB can be written without 40 MB being held anywhere.
///
/// <para>Not zeros, and not a repeating short pattern, on purpose. A server or a link that drops,
/// duplicates or reorders a block produces a file that hashes differently only if the blocks differ
/// from each other - and a netascii translation shows up only if the payload contains the bytes it
/// rewrites, which every 256 bytes of this does.</para>
/// </summary>
internal sealed class TftpProbePattern(long length) : Stream
{
    private long _position;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length { get; } = length;

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    /// <summary>The byte at <paramref name="position"/>.</summary>
    public static byte At(long position)
    {
        ulong x = unchecked((ulong)position * 0x9E3779B97F4A7C15UL);
        return unchecked((byte)((x >> 56) ^ (ulong)position));
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        int available = (int)Math.Min(count, Length - _position);
        for (int i = 0; i < available; i++)
        {
            buffer[offset + i] = At(_position + i);
        }

        _position += available;
        return available;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
