using System.Net;
using NetControl.Core.Capture;

namespace NetControl.Core.Profinet;

/// <summary>
/// PROFINET DCP over one frame channel: find devices, read one, and set one's name or address.
///
/// <para><b>The same rules as the EtherNet/IP side.</b> Identify is discovery and goes to the DCP
/// multicast address, which every PROFINET device answers - that is a read. Every Set goes to one
/// device's own MAC, never to a multicast or broadcast address; this class refuses one, and the tab
/// in front of it asks first, naming the device. A Set is followed by a Get of the same device, and
/// only a Get that shows the new value calls it done - the same readback rule as Set static.</para>
///
/// <para>Signal (flash the LED) is a Set too, and goes through the same door, but changes nothing
/// that is stored; it is how somebody at the panel finds which of twelve identical modules is the
/// one on the screen.</para>
/// </summary>
public sealed class DcpClient
{
    private readonly IFrameChannel _channel;
    private uint _xid = (uint)Random.Shared.Next(0x1000, 0x7FFFFFFF);

    public DcpClient(IFrameChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        _channel = channel;
    }

    /// <summary>How long each device may take to answer a Get or a Set.</summary>
    public TimeSpan ReplyTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The response-delay factor sent with Identify All: each device waits a random part of
    /// factor x 10 ms before answering, so a hundred devices do not answer in the same millisecond.
    /// </summary>
    public ushort IdentifyResponseDelay { get; init; } = 0x0040;

    /// <summary>Identify All. Collects answers for <paramref name="window"/>, one device per MAC.</summary>
    public async Task<IReadOnlyList<DcpDevice>> IdentifyAsync(TimeSpan window, CancellationToken cancellationToken = default)
    {
        uint xid = NextXid();
        var found = new Dictionary<MacAddress, DcpDevice>();
        // A plain object, not System.Threading.Lock: a lock on Lock is a ref struct scope, and this
        // is an async method.
        object gate = new();

        void OnFrame(object? sender, FrameEventArgs e)
        {
            if (Read(e.Frame, out MacAddress from, out ReadOnlyMemory<byte> payload)
                && Matches(payload.Span, DcpFrame.FrameIdIdentifyResponse, DcpFrame.ServiceIdentify, xid)
                && DcpFrame.TryParseDevice(payload.Span, from, out DcpDevice? device) && device is not null)
            {
                lock (gate)
                {
                    found[from] = device;
                }
            }
        }

        _channel.FrameReceived += OnFrame;

        try
        {
            _channel.Send(DcpFrame.IdentifyAll(_channel.LocalMac, xid, IdentifyResponseDelay));
            await Task.Delay(window, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _channel.FrameReceived -= OnFrame;
        }

        lock (gate)
        {
            return [.. found.Values.OrderBy(d => d.NameOfStation, StringComparer.Ordinal).ThenBy(d => d.Mac.ToString(), StringComparer.Ordinal)];
        }
    }

    /// <summary>Reads one device's name, address, type and id. Null when it does not answer.</summary>
    public async Task<DcpDevice?> GetAsync(MacAddress target, CancellationToken cancellationToken = default)
    {
        EnsureOneDevice(target);

        for (int attempt = 0; attempt < 2; attempt++)
        {
            uint xid = NextXid();
            byte[]? payload = await ExchangeAsync(DcpFrame.Get(target, _channel.LocalMac, xid), target, DcpFrame.ServiceGet, xid, cancellationToken)
                .ConfigureAwait(false);

            if (payload is not null && DcpFrame.TryParseDevice(payload, target, out DcpDevice? device))
            {
                return device;
            }
        }

        return null;
    }

    /// <summary>Sets one device's NameOfStation. The name is checked first and nothing is sent if it is invalid.</summary>
    public Task<DcpSetResult> SetNameAsync(MacAddress target, string name, bool permanent, CancellationToken cancellationToken = default)
    {
        if (DcpStationName.Problem(name) is { } problem)
        {
            throw new ArgumentException(problem, nameof(name));
        }

        return SetAsync(target, xid => DcpFrame.SetName(target, _channel.LocalMac, xid, name, permanent), cancellationToken);
    }

    /// <summary>Sets one device's address, mask and gateway. Checked first; nothing is sent if they do not make sense.</summary>
    public Task<DcpSetResult> SetIpAsync(
        MacAddress target, IPAddress address, IPAddress mask, IPAddress? gateway, bool permanent, CancellationToken cancellationToken = default)
    {
        if (IpProblem(address, mask, gateway) is { } problem)
        {
            throw new ArgumentException(problem, nameof(address));
        }

        IPAddress gw = gateway ?? IPAddress.Any;
        return SetAsync(target, xid => DcpFrame.SetIp(target, _channel.LocalMac, xid, address, mask, gw, permanent), cancellationToken);
    }

    /// <summary>Flashes the device's LED. Stores nothing.</summary>
    public Task<DcpSetResult> SignalAsync(MacAddress target, CancellationToken cancellationToken = default) =>
        SetAsync(target, xid => DcpFrame.Signal(target, _channel.LocalMac, xid), cancellationToken);

    /// <summary>What is wrong with an address set, or null. Public so the tab can say it before anyone confirms.</summary>
    public static string? IpProblem(IPAddress? address, IPAddress? mask, IPAddress? gateway)
    {
        if (address is null || mask is null)
        {
            return "An address and a mask are both needed.";
        }

        if (!Ipv4Subnet.IsContiguousMask(mask) || mask.Equals(IPAddress.Any))
        {
            return $"{mask} is not a subnet mask.";
        }

        if (!Reachability.UnicastTarget.TryCheck(address, mask, out string? notOneHost))
        {
            return notOneHost;
        }

        if (gateway is not null && !gateway.Equals(IPAddress.Any)
            && (!Ipv4Subnet.TryCreate(address, mask, out Ipv4Subnet subnet) || !subnet.Contains(gateway)))
        {
            return $"The gateway {gateway} is not on {address}'s subnet, so the device could never reach it.";
        }

        return null;
    }

    private async Task<DcpSetResult> SetAsync(MacAddress target, Func<uint, byte[]> build, CancellationToken cancellationToken)
    {
        EnsureOneDevice(target);

        // One send, no retry. A Set is a write; sending it twice because the first answer was slow
        // is how a device ends up with a write nobody can account for. The caller reads it back.
        uint xid = NextXid();
        byte[]? payload = await ExchangeAsync(build(xid), target, DcpFrame.ServiceSet, xid, cancellationToken).ConfigureAwait(false);

        if (payload is null)
        {
            return DcpSetResult.NoAnswer(target);
        }

        byte code = DcpFrame.SetResponseError(payload) ?? 0xFF;
        return new DcpSetResult(code == 0, code, DcpSetResult.ErrorText(code));
    }

    /// <summary>Sends one frame to one device and waits for the response with the same Xid from that device.</summary>
    private async Task<byte[]?> ExchangeAsync(byte[] frame, MacAddress target, byte service, uint xid, CancellationToken cancellationToken)
    {
        var answer = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnFrame(object? sender, FrameEventArgs e)
        {
            if (Read(e.Frame, out MacAddress from, out ReadOnlyMemory<byte> payload)
                && from == target
                && Matches(payload.Span, DcpFrame.FrameIdGetSet, service, xid))
            {
                answer.TrySetResult(payload.ToArray());
            }
        }

        _channel.FrameReceived += OnFrame;

        try
        {
            _channel.Send(frame);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ReplyTimeout);

            try
            {
                return await answer.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return null;
            }
        }
        finally
        {
            _channel.FrameReceived -= OnFrame;
        }
    }

    private bool Read(byte[] frame, out MacAddress from, out ReadOnlyMemory<byte> payload)
    {
        payload = default;
        from = default;

        if (!EthernetFrame.TryRead(frame, out EthernetFrame header)
            || header.EtherType != EthernetFrame.Profinet
            || header.Source == _channel.LocalMac)
        {
            return false;
        }

        from = header.Source;
        payload = frame.AsMemory(header.PayloadOffset);
        return true;
    }

    private static bool Matches(ReadOnlySpan<byte> payload, ushort frameId, byte service, uint xid) =>
        DcpFrame.TryReadHeader(payload, out DcpFrame.Header header)
        && header.FrameId == frameId
        && header.ServiceId == service
        && header.Xid == xid
        && (header.ServiceType & 0x01) != 0;

    private static void EnsureOneDevice(MacAddress target)
    {
        if (target.IsEmpty || target.IsMulticast || target.IsBroadcast)
        {
            throw new ArgumentException($"{target} is not one device. DCP Get and Set go to one device's own MAC address.", nameof(target));
        }
    }

    private uint NextXid() => Interlocked.Increment(ref _xid);
}
