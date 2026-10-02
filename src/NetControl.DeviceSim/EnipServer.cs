using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace NetControl.DeviceSim;

/// <summary>
/// Serves the device over EtherNet/IP: TCP 44818 for CIP requests, UDP 44818 for
/// ListIdentity discovery.
///
/// All framing work is in non-async static helpers - Span&lt;T&gt; locals are illegal
/// inside async methods (CS4013).
/// </summary>
// A primary constructor's parameter list cannot see the type's own members, so `int port = Port`
// does not compile (CS0103). Nullable, defaulted in the field below, keeps the 44818 in one place.
public sealed class EnipServer(SimulatedDevice device, Action<string> log, IPAddress? bindAddress = null,
                              int? port = null)
{
    public const int Port = 44818;

    /// <summary>
    /// The port actually served. Defaults to the standard 44818; overridden so a test can start a
    /// simulator in-process on a spare loopback port and drive the real client against it, which is
    /// what makes the commissioning sequence testable with no hardware and no ports to fight over.
    /// </summary>
    private readonly int _port = port ?? Port;

    /// <summary>
    /// Defaults to all interfaces. Set it to one of the machine's addresses to run several
    /// simulators side by side - TCP 44818 can only be bound once per address.
    /// </summary>
    private readonly IPAddress _bind = bindAddress ?? IPAddress.Any;

    private const ushort CmdListIdentity      = 0x0063;
    private const ushort CmdRegisterSession   = 0x0065;
    private const ushort CmdUnRegisterSession = 0x0066;
    private const ushort CmdSendRRData        = 0x006F;
    private const ushort ItemNullAddress      = 0x0000;
    private const ushort ItemUnconnectedData  = 0x00B2;
    private const ushort ItemListIdentity     = 0x000C;
    private const int HeaderLength = 24;

    private uint _nextSession = 0x1A2B0000;

    public async Task RunAsync(CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var tcp = ServeTcpAsync(stop.Token);
        var udp = ServeUdpAsync(stop.Token);

        // If either listener gives up - nearly always because the port is already
        // taken - bring the other down too. A half-running simulator that answers
        // discovery but refuses connections is worse than one that exits saying why.
        await Task.WhenAny(tcp, udp);
        await stop.CancelAsync();

        try { await Task.WhenAll(tcp, udp); }
        catch (OperationCanceledException) { }
    }

    // =======================================================================
    // TCP - CIP requests
    // =======================================================================
    private async Task ServeTcpAsync(CancellationToken ct)
    {
        var listener = new TcpListener(_bind, _port);
        try
        {
            listener.Start();
        }
        catch (SocketException ex)
        {
            log($"ERROR: cannot listen on TCP {_bind}:{_port} - {ex.SocketErrorCode}");
            if (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
                log("       Another simulator instance or an EtherNet/IP tool already holds that port.");
            return;
        }
        log($"CIP server listening on TCP {_bind}:{_port}");

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                _ = HandleClientAsync(client, ct);
            }
        }
        catch (OperationCanceledException) { }
        finally { listener.Stop(); }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
        log($"  connection from {remote}");
        uint session = 0;

        try
        {
            using (client)
            {
                client.NoDelay = true;
                var stream = client.GetStream();
                var header = new byte[HeaderLength];

                while (!ct.IsCancellationRequested)
                {
                    if (!await ReadExactAsync(stream, header, ct)) break;

                    ushort command = BinaryPrimitives.ReadUInt16LittleEndian(header);
                    ushort length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2));
                    uint inSession = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));

                    var payload = new byte[length];
                    if (length > 0 && !await ReadExactAsync(stream, payload, ct)) break;

                    if (device.ResponseDelayMs > 0) await Task.Delay(device.ResponseDelayMs, ct);

                    switch (command)
                    {
                        case CmdRegisterSession:
                            session = unchecked(++_nextSession);
                            log($"  RegisterSession -> handle 0x{session:X8}");
                            await stream.WriteAsync(BuildFrame(CmdRegisterSession, payload, session, 0), ct);
                            break;

                        case CmdUnRegisterSession:
                            log("  UnRegisterSession");
                            return;

                        case CmdListIdentity:
                            await stream.WriteAsync(BuildFrame(CmdListIdentity, BuildIdentityItem(device, _port), inSession, 0), ct);
                            break;

                        case CmdSendRRData:
                        {
                            if (inSession != session || session == 0)
                            {
                                log($"  SendRRData with bad session 0x{inSession:X8} -> status 0x64");
                                await stream.WriteAsync(BuildFrame(CmdSendRRData, [], inSession, 0x64), ct);
                                break;
                            }

                            byte[]? cipRequest = ExtractCipRequest(payload);
                            if (cipRequest is null) { log("  malformed CPF"); break; }

                            var (reply, dropAfter, resetAfter) = Dispatch(cipRequest);
                            await stream.WriteAsync(BuildFrame(CmdSendRRData, WrapCipReply(reply), session, 0), ct);

                            if (resetAfter)
                            {
                                log("  Identity reset - applying pending configuration, dropping connection");
                                device.ResetDevice();
                                return;
                            }
                            if (dropAfter)
                            {
                                log("  quirk DropConnectionOnWrite - closing mid-exchange");
                                return;
                            }
                            break;
                        }

                        default:
                            log($"  unsupported encapsulation command 0x{command:X4}");
                            await stream.WriteAsync(BuildFrame(command, [], inSession, 0x01), ct);
                            break;
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (SocketException) { }
        finally { log($"  connection closed ({remote})"); }
    }

    /// <summary>Returns the CIP reply bytes, plus whether to drop the connection or reset after sending.</summary>
    private (byte[] Reply, bool Drop, bool Reset) Dispatch(byte[] cipRequest)
    {
        if (!CipCodec.TryParseRequest(cipRequest, out byte service, out var path, out byte[] data))
            return (CipCodec.BuildReply(0, CipStatus.PathSegmentError), false, false);

        log($"  CIP service 0x{service:X2} on {path}");

        // Identity object
        if (path.Class == 0x01)
        {
            if (service == CipCodec.Reset) return (CipCodec.BuildReply(service, CipStatus.Success), false, true);

            if (service == CipCodec.GetAttributeAll)
                return (CipCodec.BuildReply(service, CipStatus.Success, device.SerializeIdentity()), false, false);

            if (service == CipCodec.GetAttributeSingle)
            {
                byte[]? v = path.Attribute switch
                {
                    1 => CipCodec.U16(device.VendorId),
                    2 => CipCodec.U16(device.DeviceType),
                    3 => CipCodec.U16(device.ProductCode),
                    4 => [device.MajorRevision, device.MinorRevision],
                    5 => CipCodec.U16(device.DeviceStatus),
                    6 => CipCodec.U32(device.SerialNumber),
                    7 => CipCodec.CipShortString(device.ProductName),
                    _ => null
                };
                return v is null
                    ? (CipCodec.BuildReply(service, CipStatus.AttributeNotSupported), false, false)
                    : (CipCodec.BuildReply(service, CipStatus.Success, v), false, false);
            }
            return (CipCodec.BuildReply(service, CipStatus.ServiceNotSupported), false, false);
        }

        // TCP/IP Interface object
        if (path.Class == 0xF5)
        {
            if (service == CipCodec.GetAttributeSingle)
            {
                byte[]? v = path.Attribute switch
                {
                    1 => CipCodec.U32(device.InterfaceStatus),
                    2 => CipCodec.U32((uint)device.Capability),
                    3 => CipCodec.U32((uint)device.Method),
                    4 => [0x20, 0xF6, 0x24, 0x01],           // path to the Ethernet Link object
                    5 => device.SerializeInterfaceConfig(),
                    6 => CipCodec.CipString(device.HostName),
                    _ => null
                };
                return v is null
                    ? (CipCodec.BuildReply(service, CipStatus.AttributeNotSupported), false, false)
                    : (CipCodec.BuildReply(service, CipStatus.Success, v), false, false);
            }

            if (service == CipCodec.SetAttributeSingle)
            {
                bool drop = device.Quirks.HasFlag(Quirk.DropConnectionOnWrite);
                byte status = path.Attribute switch
                {
                    3 => device.ApplyConfigControl(data),
                    5 => device.ApplyInterfaceConfig(data),
                    6 => CipStatus.Success,
                    1 or 2 or 4 => CipStatus.AttributeNotSettable,
                    _ => CipStatus.AttributeNotSupported
                };

                if (status == CipStatus.Success)
                {
                    // Say plainly when a quirk means "success" did not change anything -
                    // otherwise the simulator's own log reads like the write landed.
                    string note = path.Attribute != 5 ? ""
                        : device.Quirks.HasFlag(Quirk.LiesAboutWriteSuccess)
                            ? "   (quirk: success reported, write discarded)"
                        : device.Quirks.HasFlag(Quirk.RequiresResetToApply)
                            ? "   (quirk: held pending until an Identity reset)"
                        : "";
                    log($"    accepted -> now {device.Ip}/{device.Mask} method={device.Method}{note}");
                }
                else
                    log($"    refused with status 0x{status:X2}");

                return (CipCodec.BuildReply(service, status), drop, false);
            }
            return (CipCodec.BuildReply(service, CipStatus.ServiceNotSupported), false, false);
        }

        // Ethernet Link object - one instance per port. Read-only here, as it is on most devices:
        // nothing in NetControl writes to it, and Get_and_Clear (0x4C) is deliberately not served
        // because nothing in NetControl may send it.
        if (path.Class == 0xF6)
        {
            if (path.Instance < 1 || path.Instance > device.LinkPorts.Count)
                return (CipCodec.BuildReply(service, CipStatus.ObjectDoesNotExist), false, false);

            SimulatedLinkPort port = device.LinkPorts[path.Instance - 1];
            bool counters = !device.Quirks.HasFlag(Quirk.NoLinkCounters);

            if (service == CipCodec.GetAttributeSingle)
            {
                byte[]? v = path.Attribute switch
                {
                    1 => CipCodec.U32(port.SpeedMbps),                   // Mb/s, which is the spec's unit
                    2 => CipCodec.U32(port.Flags),
                    3 => port.Mac,
                    4 when counters => SimulatedLinkPort.Pack(port.InterfaceCounters),
                    5 when counters => SimulatedLinkPort.Pack(port.MediaCounters),
                    _ => null
                };
                return v is null
                    ? (CipCodec.BuildReply(service, CipStatus.AttributeNotSupported), false, false)
                    : (CipCodec.BuildReply(service, CipStatus.Success, v), false, false);
            }
            return (CipCodec.BuildReply(service, CipStatus.ServiceNotSupported), false, false);
        }

        return (CipCodec.BuildReply(service, CipStatus.PathDestinationUnknown), false, false);
    }

    // =======================================================================
    // UDP - ListIdentity discovery
    // =======================================================================
    private async Task ServeUdpAsync(CancellationToken ct)
    {
        using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        udp.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.EnableBroadcast = true;

        try { udp.Bind(new IPEndPoint(_bind, _port)); }
        catch (SocketException ex)
        {
            log($"WARNING: cannot bind UDP {_port} ({ex.SocketErrorCode}); discovery disabled.");
            return;
        }
        log($"ListIdentity responder on UDP {_bind}:{_port}");

        var buf = new byte[1024];
        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult r;
            try { r = await udp.ReceiveFromAsync(buf, new IPEndPoint(IPAddress.Any, 0), ct); }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { continue; }

            if (r.ReceivedBytes < HeaderLength) continue;
            ushort command = BinaryPrimitives.ReadUInt16LittleEndian(buf);
            if (command != CmdListIdentity) continue;

            if (device.Quirks.HasFlag(Quirk.IgnoresBroadcastDiscovery))
            {
                log($"  ListIdentity from {r.RemoteEndPoint} ignored (quirk IgnoresBroadcastDiscovery)");
                continue;
            }

            if (device.ResponseDelayMs > 0) await Task.Delay(device.ResponseDelayMs, ct);

            log($"  ListIdentity from {r.RemoteEndPoint} -> replying as {device.Ip}");
            var reply = BuildFrame(CmdListIdentity, BuildIdentityItem(device, _port), 0, 0);
            try { await udp.SendToAsync(reply, r.RemoteEndPoint, ct); }
            catch (SocketException) { /* requester vanished */ }
        }
    }

    // =======================================================================
    // Framing helpers - non-async on purpose (CS4013)
    // =======================================================================
    private static byte[] BuildFrame(ushort command, byte[] data, uint session, uint status)
    {
        var frame = new byte[HeaderLength + data.Length];
        var h = frame.AsSpan();
        BinaryPrimitives.WriteUInt16LittleEndian(h, command);
        BinaryPrimitives.WriteUInt16LittleEndian(h[2..], (ushort)data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(h[4..], session);
        BinaryPrimitives.WriteUInt32LittleEndian(h[8..], status);
        // h[12..20] sender context - a real device echoes it; nothing here depends on that
        BinaryPrimitives.WriteUInt32LittleEndian(h[20..], 0);
        data.CopyTo(h[HeaderLength..]);
        return frame;
    }

    /// <summary>Pulls the unconnected data item out of a SendRRData payload.</summary>
    private static byte[]? ExtractCipRequest(byte[] payload)
    {
        if (payload.Length < 8) return null;
        var p = payload.AsSpan(6);          // skip interface handle (4) + timeout (2)

        ushort itemCount = BinaryPrimitives.ReadUInt16LittleEndian(p);
        int i = 2;
        for (int n = 0; n < itemCount; n++)
        {
            if (i + 4 > p.Length) return null;
            ushort type = BinaryPrimitives.ReadUInt16LittleEndian(p[i..]);
            ushort len = BinaryPrimitives.ReadUInt16LittleEndian(p[(i + 2)..]);
            i += 4;
            if (i + len > p.Length) return null;
            if (type == ItemUnconnectedData) return p.Slice(i, len).ToArray();
            i += len;
        }
        return null;
    }

    /// <summary>Wraps a CIP reply back into interface handle + timeout + CPF.</summary>
    private static byte[] WrapCipReply(byte[] cip)
    {
        var data = new byte[4 + 2 + 2 + 4 + 4 + cip.Length];
        var s = data.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(s, 0);          // interface handle
        BinaryPrimitives.WriteUInt16LittleEndian(s[4..], 0);     // timeout
        BinaryPrimitives.WriteUInt16LittleEndian(s[6..], 2);     // item count
        BinaryPrimitives.WriteUInt16LittleEndian(s[8..], ItemNullAddress);
        BinaryPrimitives.WriteUInt16LittleEndian(s[10..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(s[12..], ItemUnconnectedData);
        BinaryPrimitives.WriteUInt16LittleEndian(s[14..], (ushort)cip.Length);
        cip.CopyTo(s[16..]);
        return data;
    }

    /// <summary>
    /// The ListIdentity item (type 0x000C). Note the sockaddr_in inside it is BIG-endian,
    /// sitting in the middle of an otherwise little-endian structure.
    /// </summary>
    /// <param name="d">The device to describe.</param>
    /// <param name="port">
    /// The port this simulator is actually serving, which is what goes in the sockaddr_in. Passed
    /// in because this stays a static helper - all the span work does, since Span&lt;T&gt; locals
    /// are illegal in an async method.
    /// </param>
    private static byte[] BuildIdentityItem(SimulatedDevice d, int port)
    {
        var name = CipCodec.CipShortString(d.ProductName);
        int bodyLen = 2 + 16 + 2 + 2 + 2 + 2 + 2 + 4 + name.Length + 1;

        var item = new byte[6 + bodyLen];
        var s = item.AsSpan();
        BinaryPrimitives.WriteUInt16LittleEndian(s, 1);                     // item count
        BinaryPrimitives.WriteUInt16LittleEndian(s[2..], ItemListIdentity);
        BinaryPrimitives.WriteUInt16LittleEndian(s[4..], (ushort)bodyLen);

        var b = s[6..];
        BinaryPrimitives.WriteUInt16LittleEndian(b, 1);                     // encapsulation version

        // sockaddr_in - big-endian
        BinaryPrimitives.WriteInt16BigEndian(b[2..], 2);                    // AF_INET
        BinaryPrimitives.WriteUInt16BigEndian(b[4..], (ushort)port);
        d.Ip.GetAddressBytes().CopyTo(b[6..]);                              // already network order
        // b[10..18] sin_zero stays zero

        BinaryPrimitives.WriteUInt16LittleEndian(b[18..], d.VendorId);
        BinaryPrimitives.WriteUInt16LittleEndian(b[20..], d.DeviceType);
        BinaryPrimitives.WriteUInt16LittleEndian(b[22..], d.ProductCode);
        b[24] = d.MajorRevision;
        b[25] = d.MinorRevision;
        BinaryPrimitives.WriteUInt16LittleEndian(b[26..], d.DeviceStatus);
        BinaryPrimitives.WriteUInt32LittleEndian(b[28..], d.SerialNumber);
        name.CopyTo(b[32..]);
        b[32 + name.Length] = 3;                                            // state: operational

        return item;
    }

    private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        int off = 0;
        while (off < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(off), ct);
            if (n == 0) return false;
            off += n;
        }
        return true;
    }
}
