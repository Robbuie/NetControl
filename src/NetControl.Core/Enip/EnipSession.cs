using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using NetControl.Core.Cip;

namespace NetControl.Core.Enip;

/// <summary>
/// One registered EtherNet/IP session to one device, over TCP 44818.
///
/// <para><b>Unicast, always, to an address the user named.</b> There is no constructor here that
/// takes a broadcast address or a range. Discovery is a broadcast; configuration is a conversation
/// with one device, and the type makes that the only thing it can be.</para>
///
/// <para><b>Everything on this wire is little-endian</b> - the encapsulation header, the CPF
/// items, and the addresses inside CIP attributes, which is the reverse of what socket code
/// conditions you to expect. The one exception in the whole protocol is the <c>sockaddr_in</c>
/// inside a ListIdentity reply, which is big-endian sitting in the middle of all this - see
/// <see cref="ListIdentityReply"/>, which is the only thing allowed to read it.</para>
///
/// <para>All framing lives in non-async static helpers. <c>Span&lt;T&gt;</c> locals are illegal in
/// an async method (CS4013), so the encode and decode steps stay out of the state machine.</para>
/// </summary>
public sealed class EnipSession : IDisposable
{
    public const int DefaultPort = 44818;

    private const ushort CommandRegisterSession = 0x0065;
    private const ushort CommandUnRegisterSession = 0x0066;
    private const ushort CommandSendRRData = 0x006F;

    private const ushort ItemNullAddress = 0x0000;
    private const ushort ItemUnconnectedData = 0x00B2;

    private const int HeaderLength = 24;

    /// <summary>
    /// How long the device is told it may take over the CPF, in seconds. Not our own timeout -
    /// that is the cancellation token - but what we promise to wait before giving up on it.
    /// </summary>
    private const ushort DeviceTimeoutSeconds = 5;

    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private uint _sessionHandle;
    private bool _disposed;

    private EnipSession(IPAddress address, int port, TcpClient tcp)
    {
        Address = address;
        Port = port;
        _tcp = tcp;
        _stream = tcp.GetStream();
    }

    /// <summary>The one device this session talks to.</summary>
    public IPAddress Address { get; }

    public int Port { get; }

    public uint SessionHandle => _sessionHandle;

    /// <summary>
    /// Opens a TCP connection to one device and registers a session.
    /// </summary>
    /// <param name="address">The device. A single unicast address, never a broadcast.</param>
    /// <param name="port">Almost always <see cref="DefaultPort"/>; a parameter so tests can run a simulator.</param>
    /// <param name="timeout">How long to wait for the connection and the registration.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static async Task<EnipSession> ConnectAsync(
        IPAddress address,
        int port = DefaultPort,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.Equals(IPAddress.Broadcast) || address.Equals(IPAddress.Any))
        {
            throw new EnipException($"'{address}' is not one device, and configuration is never broadcast.")
            {
                Remediation = "Select the device you mean, or run a discovery scan to find its address first.",
            };
        }

        TimeSpan limit = timeout ?? TimeSpan.FromSeconds(3);

        var tcp = new TcpClient { NoDelay = true };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(limit);

        try
        {
            await tcp.ConnectAsync(address, port, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            tcp.Dispose();
            throw new EnipException($"{address} did not answer on TCP/{port} within {limit.TotalSeconds:0.#}s.")
            {
                Remediation = "Check the device is powered and on this subnet, and that your laptop has a route "
                    + "to it - a device just given an address on another subnet is unreachable until you do.",
            };
        }
        catch (SocketException ex)
        {
            tcp.Dispose();
            throw new EnipException($"Cannot reach {address} on TCP/{port}: {ex.SocketErrorCode}.", ex)
            {
                Remediation = "Check the address and the cable. A device that answers BOOTP may still refuse "
                    + "TCP if it has only just come up.",
            };
        }
        catch
        {
            tcp.Dispose();
            throw;
        }

        var session = new EnipSession(address, port, tcp);

        try
        {
            await session.RegisterAsync(deadline.Token).ConfigureAwait(false);
        }
        catch
        {
            session.Dispose();
            throw;
        }

        return session;
    }

    /// <summary>Sends one unconnected (UCMM) CIP request and returns what the device said.</summary>
    public async Task<CipResponse> SendAsync(CipRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);

        byte[] frame = BuildRRData(request.Serialize());

        (uint status, byte[] payload) =
            await RoundTripAsync(CommandSendRRData, frame, _sessionHandle, cancellationToken).ConfigureAwait(false);

        if (status != 0)
        {
            throw new EnipException(
                $"{Address} refused the request at the encapsulation layer (status 0x{status:X8}).");
        }

        return ExtractDataItem(payload);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            if (_sessionHandle != 0)
            {
                // Politeness, not correctness: a device has a small pool of session slots and
                // leaking them means the fifth commissioning attempt of the day fails to register.
                var frame = new byte[HeaderLength];
                BinaryPrimitives.WriteUInt16LittleEndian(frame, CommandUnRegisterSession);
                BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), _sessionHandle);
                _stream.Write(frame);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            // The connection is going away regardless, and it very often already has - a device
            // that has just been given a new address drops the old one mid-goodbye.
        }

        _stream.Dispose();
        _tcp.Dispose();
    }

    /// <summary>
    /// interface handle (UDINT, 0 for CIP) + timeout (UINT, seconds), then a CPF carrying a null
    /// address item and the unconnected data item.
    /// </summary>
    private static byte[] BuildRRData(byte[] cip)
    {
        var data = new byte[16 + cip.Length];
        Span<byte> s = data;

        BinaryPrimitives.WriteUInt32LittleEndian(s, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(s[4..], DeviceTimeoutSeconds);
        BinaryPrimitives.WriteUInt16LittleEndian(s[6..], 2);
        BinaryPrimitives.WriteUInt16LittleEndian(s[8..], ItemNullAddress);
        BinaryPrimitives.WriteUInt16LittleEndian(s[10..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(s[12..], ItemUnconnectedData);
        BinaryPrimitives.WriteUInt16LittleEndian(s[14..], (ushort)cip.Length);
        cip.CopyTo(s[16..]);

        return data;
    }

    /// <summary>Skips the interface handle and timeout, then walks the CPF looking for the data item.</summary>
    private static CipResponse ExtractDataItem(byte[] payload)
    {
        if (payload.Length < 8)
        {
            throw new EnipException($"SendRRData reply is {payload.Length} bytes, too short to hold a CPF.");
        }

        ReadOnlySpan<byte> items = payload.AsSpan(6);
        ushort count = BinaryPrimitives.ReadUInt16LittleEndian(items);
        int at = 2;

        for (int n = 0; n < count; n++)
        {
            if (at + 4 > items.Length)
            {
                break;
            }

            ushort type = BinaryPrimitives.ReadUInt16LittleEndian(items[at..]);
            ushort length = BinaryPrimitives.ReadUInt16LittleEndian(items[(at + 2)..]);
            at += 4;

            if (at + length > items.Length)
            {
                break;
            }

            if (type == ItemUnconnectedData)
            {
                return CipResponse.Parse(items.Slice(at, length));
            }

            at += length;
        }

        throw new EnipException(
            "The device replied without an unconnected data item, so there is no CIP status in it.");
    }

    private static byte[] BuildFrame(ushort command, byte[] data, uint session)
    {
        var frame = new byte[HeaderLength + data.Length];
        Span<byte> h = frame;

        BinaryPrimitives.WriteUInt16LittleEndian(h, command);
        BinaryPrimitives.WriteUInt16LittleEndian(h[2..], (ushort)data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(h[4..], session);
        BinaryPrimitives.WriteUInt32LittleEndian(h[8..], 0);     // status, zero on a request
        // h[12..20] is the sender context, left zero: nothing here pipelines requests.
        BinaryPrimitives.WriteUInt32LittleEndian(h[20..], 0);    // options

        data.CopyTo(h[HeaderLength..]);
        return frame;
    }

    private async Task RegisterAsync(CancellationToken cancellationToken)
    {
        // Protocol version (UINT, must be 1) and options flags (UINT, must be 0).
        var data = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(data, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2), 0);

        (uint status, _) =
            await RoundTripAsync(CommandRegisterSession, data, 0, cancellationToken).ConfigureAwait(false);

        if (status != 0)
        {
            throw new EnipException($"{Address} refused to register a session (status 0x{status:X8}).")
            {
                Remediation = "The device may have run out of session slots. Power-cycle it, or wait for the "
                    + "sessions another tool left open to time out.",
            };
        }

        if (_sessionHandle == 0)
        {
            throw new EnipException($"{Address} returned a zero session handle, which cannot be used.");
        }
    }

    private async Task<(uint Status, byte[] Payload)> RoundTripAsync(
        ushort command, byte[] data, uint session, CancellationToken cancellationToken)
    {
        byte[] frame = BuildFrame(command, data, session);

        try
        {
            await _stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);

            var header = new byte[HeaderLength];
            await ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);

            ushort length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2));
            uint replySession = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
            uint status = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));

            if (command == CommandRegisterSession)
            {
                _sessionHandle = replySession;
            }

            var payload = new byte[length];
            if (length > 0)
            {
                await ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
            }

            return (status, payload);
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            throw new EnipException($"The connection to {Address} failed mid-exchange: {ex.Message}", ex)
            {
                Remediation = "Some adapters drop the connection during a configuration write. Re-read the device "
                    + "before assuming the write did not land.",
            };
        }
    }

    private async Task ReadExactlyAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        int offset = 0;

        while (offset < buffer.Length)
        {
            int read = await _stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);

            if (read == 0)
            {
                throw new EnipException($"{Address} closed the connection part-way through a message.");
            }

            offset += read;
        }
    }
}
