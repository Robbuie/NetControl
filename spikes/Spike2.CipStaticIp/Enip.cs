using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NetControl.Spike.CipStaticIp;

/// <summary>
/// EtherNet/IP encapsulation layer (Volume 2) over TCP 44818.
/// Everything on the wire here is LITTLE-endian, including the IP addresses
/// carried inside CIP attributes — which is the opposite of what you expect
/// coming from normal socket code, and a very common source of bugs.
/// </summary>
public sealed class EnipSession : IDisposable
{
    public const int Port = 44818;

    private const ushort CmdRegisterSession   = 0x0065;
    private const ushort CmdUnRegisterSession = 0x0066;
    private const ushort CmdSendRRData        = 0x006F;

    private const ushort ItemNullAddress     = 0x0000;
    private const ushort ItemUnconnectedData = 0x00B2;

    private const int HeaderLength = 24;

    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private uint _sessionHandle;

    public IPAddress Address { get; }
    public uint SessionHandle => _sessionHandle;

    private EnipSession(IPAddress address, TcpClient tcp)
    {
        Address = address;
        _tcp = tcp;
        _stream = tcp.GetStream();
    }

    public static async Task<EnipSession> ConnectAsync(IPAddress address, int timeoutMs = 3000,
                                                       CancellationToken ct = default)
    {
        var tcp = new TcpClient { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutMs);

        try { await tcp.ConnectAsync(address, Port, timeout.Token); }
        catch (OperationCanceledException) { tcp.Dispose(); throw new TimeoutException($"No TCP/{Port} response from {address}"); }
        catch { tcp.Dispose(); throw; }

        var s = new EnipSession(address, tcp);
        await s.RegisterAsync(ct);
        return s;
    }

    private async Task RegisterAsync(CancellationToken ct)
    {
        // Data = protocol version (UINT, must be 1) + options flags (UINT, must be 0)
        var data = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(data, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2), 0);

        var (status, payload) = await RoundTripAsync(CmdRegisterSession, data, 0, ct);
        if (status != 0) throw new EnipException($"RegisterSession failed, encapsulation status 0x{status:X8}");
        if (payload.Length < 4) throw new EnipException("RegisterSession returned no session handle");

        // The handle comes back in the header, which RoundTripAsync captured for us.
        if (_sessionHandle == 0) throw new EnipException("Device returned a zero session handle");
    }

    /// <summary>Sends one unconnected (UCMM) CIP request and returns the CIP response.</summary>
    public async Task<CipResponse> SendUnconnectedAsync(CipRequest request, CancellationToken ct = default)
    {
        // Note: all the span work lives in non-async helpers below. Span<T> locals
        // are illegal inside async methods (CS4013), so the encode/decode steps have
        // to be kept out of the state machine.
        byte[] data = BuildRRData(request.Serialize());

        var (status, payload) = await RoundTripAsync(CmdSendRRData, data, _sessionHandle, ct);
        if (status != 0) throw new EnipException($"SendRRData failed, encapsulation status 0x{status:X8}");

        return ExtractDataItem(payload);
    }

    /// <summary>
    /// interface handle (UDINT, 0 = CIP) + timeout (UINT, seconds)
    /// + CPF: item count, null address item, unconnected data item.
    /// </summary>
    private static byte[] BuildRRData(byte[] cip)
    {
        var data = new byte[4 + 2 + 2 + 4 + 4 + cip.Length];
        var s = data.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(s, 0);              // interface handle
        BinaryPrimitives.WriteUInt16LittleEndian(s[4..], 5);         // timeout, seconds
        BinaryPrimitives.WriteUInt16LittleEndian(s[6..], 2);         // CPF item count
        BinaryPrimitives.WriteUInt16LittleEndian(s[8..], ItemNullAddress);
        BinaryPrimitives.WriteUInt16LittleEndian(s[10..], 0);        // null address length
        BinaryPrimitives.WriteUInt16LittleEndian(s[12..], ItemUnconnectedData);
        BinaryPrimitives.WriteUInt16LittleEndian(s[14..], (ushort)cip.Length);
        cip.CopyTo(s[16..]);
        return data;
    }

    /// <summary>Skips interface handle + timeout, then walks the CPF for the data item.</summary>
    private static CipResponse ExtractDataItem(byte[] payload)
    {
        if (payload.Length < 8) throw new EnipException("SendRRData reply too short");
        var p = payload.AsSpan(6);

        ushort itemCount = BinaryPrimitives.ReadUInt16LittleEndian(p);
        int i = 2;
        for (int n = 0; n < itemCount; n++)
        {
            if (i + 4 > p.Length) break;
            ushort type = BinaryPrimitives.ReadUInt16LittleEndian(p[i..]);
            ushort len = BinaryPrimitives.ReadUInt16LittleEndian(p[(i + 2)..]);
            i += 4;
            if (i + len > p.Length) break;
            if (type == ItemUnconnectedData) return CipResponse.Parse(p.Slice(i, len));
            i += len;
        }
        throw new EnipException("No unconnected data item in reply");
    }

    private static byte[] BuildFrame(ushort command, byte[] data, uint session)
    {
        var frame = new byte[HeaderLength + data.Length];
        var h = frame.AsSpan();
        BinaryPrimitives.WriteUInt16LittleEndian(h, command);
        BinaryPrimitives.WriteUInt16LittleEndian(h[2..], (ushort)data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(h[4..], session);
        BinaryPrimitives.WriteUInt32LittleEndian(h[8..], 0);     // status, zero on request
        // h[12..20] sender context — left zero
        BinaryPrimitives.WriteUInt32LittleEndian(h[20..], 0);    // options
        data.CopyTo(h[HeaderLength..]);
        return frame;
    }

    private async Task<(uint Status, byte[] Payload)> RoundTripAsync(
        ushort command, byte[] data, uint session, CancellationToken ct)
    {
        byte[] frame = BuildFrame(command, data, session);
        await _stream.WriteAsync(frame, ct);

        var header = new byte[HeaderLength];
        await ReadExactAsync(header, ct);

        uint replySession = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        uint status = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
        ushort length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2));

        if (command == CmdRegisterSession) _sessionHandle = replySession;

        var payload = new byte[length];
        if (length > 0) await ReadExactAsync(payload, ct);
        return (status, payload);
    }

    private async Task ReadExactAsync(byte[] buffer, CancellationToken ct)
    {
        int off = 0;
        while (off < buffer.Length)
        {
            int n = await _stream.ReadAsync(buffer.AsMemory(off), ct);
            if (n == 0) throw new EnipException("Device closed the connection mid-message");
            off += n;
        }
    }

    public void Dispose()
    {
        try
        {
            if (_sessionHandle != 0)
            {
                var frame = new byte[HeaderLength];
                BinaryPrimitives.WriteUInt16LittleEndian(frame, CmdUnRegisterSession);
                BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), _sessionHandle);
                _stream.Write(frame);
            }
        }
        catch { /* closing anyway */ }
        _stream.Dispose();
        _tcp.Dispose();
    }

    // -----------------------------------------------------------------------
    /// <summary>
    /// ListIdentity (0x0063) broadcast on UDP 44818. Every EtherNet/IP device on the
    /// segment answers with its identity — the feature the Rockwell tool lacks entirely.
    /// </summary>
    public static async Task<List<DeviceIdentity>> DiscoverAsync(
        IPAddress? localAddress = null, int listenMs = 2000, CancellationToken ct = default)
    {
        var found = new List<DeviceIdentity>();
        var sawIp = new HashSet<string>();

        using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        udp.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.EnableBroadcast = true;
        udp.Bind(new IPEndPoint(localAddress ?? IPAddress.Any, 0));

        var request = new byte[HeaderLength];
        BinaryPrimitives.WriteUInt16LittleEndian(request, 0x0063);   // ListIdentity, no data

        udp.SendTo(request, new IPEndPoint(IPAddress.Broadcast, Port));

        var buf = new byte[1024];
        var deadline = DateTime.UtcNow.AddMilliseconds(listenMs);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            udp.ReceiveTimeout = Math.Max(100, (int)(deadline - DateTime.UtcNow).TotalMilliseconds);
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            int n;
            try { n = udp.ReceiveFrom(buf, ref from); }
            catch (SocketException) { break; }   // timeout expired

            var id = DeviceIdentity.TryParse(buf.AsSpan(0, n), ((IPEndPoint)from).Address);
            if (id is not null && sawIp.Add(id.IpAddress.ToString())) found.Add(id);
        }

        await Task.CompletedTask;
        return found;
    }
}

public sealed class EnipException(string message) : Exception(message);

/// <summary>ListIdentity reply item (type 0x000C).</summary>
public sealed record DeviceIdentity(
    IPAddress IpAddress, ushort VendorId, ushort DeviceType, ushort ProductCode,
    byte MajorRevision, byte MinorRevision, ushort Status, uint SerialNumber,
    string ProductName, byte State)
{
    public string Vendor => VendorId switch
    {
        1 => "Rockwell Automation / Allen-Bradley",
        5 => "Rockwell Automation",
        26 => "Festo",
        108 => "Schneider Electric",
        283 => "Beckhoff",
        _ => $"vendor #{VendorId}"
    };

    public static DeviceIdentity? TryParse(ReadOnlySpan<byte> frame, IPAddress from)
    {
        if (frame.Length < 24 + 2) return null;
        var p = frame[24..];                                     // skip encapsulation header

        ushort itemCount = BinaryPrimitives.ReadUInt16LittleEndian(p);
        if (itemCount < 1) return null;
        if (p.Length < 6) return null;

        ushort type = BinaryPrimitives.ReadUInt16LittleEndian(p[2..]);
        if (type != 0x000C) return null;
        var d = p[6..];
        if (d.Length < 33) return null;

        // encap version (2), then a 16-byte sockaddr_in that is BIG-endian,
        // unlike everything around it.
        var addr = new IPAddress(d.Slice(2 + 4, 4));
        var body = d[(2 + 16)..];
        if (body.Length < 15) return null;

        ushort vendorId    = BinaryPrimitives.ReadUInt16LittleEndian(body);
        ushort deviceType  = BinaryPrimitives.ReadUInt16LittleEndian(body[2..]);
        ushort productCode = BinaryPrimitives.ReadUInt16LittleEndian(body[4..]);
        byte major = body[6], minor = body[7];
        ushort status  = BinaryPrimitives.ReadUInt16LittleEndian(body[8..]);
        uint serial    = BinaryPrimitives.ReadUInt32LittleEndian(body[10..]);

        byte nameLen = body[14];                                  // SHORT_STRING
        if (body.Length < 15 + nameLen) return null;
        string name = Encoding.ASCII.GetString(body.Slice(15, nameLen));
        byte state = body.Length > 15 + nameLen ? body[15 + nameLen] : (byte)0;

        // Prefer the address the device reports; fall back to the UDP source.
        var reported = addr.Equals(IPAddress.Any) ? from : addr;
        return new DeviceIdentity(reported, vendorId, deviceType, productCode,
                                  major, minor, status, serial, name, state);
    }
}
