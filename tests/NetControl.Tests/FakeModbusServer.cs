using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace NetControl.Tests;

/// <summary>
/// A Modbus/TCP server on loopback that answers reads from a fixed register table - and remembers
/// every function code it was sent, so a test can assert that nothing but reads ever arrived.
/// </summary>
internal sealed class FakeModbusServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _running;

    private FakeModbusServer(TcpListener listener)
    {
        _listener = listener;
        _running = Task.Run(AcceptLoopAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Holding and input registers both read from here.</summary>
    public ushort[] Registers { get; } = new ushort[256];

    /// <summary>Coils and discrete inputs both read from here, one bool per address.</summary>
    public bool[] Bits { get; } = new bool[256];

    /// <summary>Every function code received, in order.</summary>
    public List<byte> FunctionsSeen { get; } = [];

    /// <summary>When set, every request is answered with this exception code.</summary>
    public byte? AnswerWithException { get; set; }

    /// <summary>When true, connections are accepted and never answered.</summary>
    public bool Silent { get; set; }

    public static FakeModbusServer Start()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new FakeModbusServer(listener);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();

        try
        {
            await _running;
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }

        _stop.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client = await _listener.AcceptTcpClientAsync(_stop.Token);
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            NetworkStream stream = client.GetStream();
            var request = new byte[12];
            int got = 0;

            while (got < request.Length)
            {
                int read = await stream.ReadAsync(request.AsMemory(got));
                if (read == 0)
                {
                    return;
                }

                got += read;
            }

            lock (FunctionsSeen)
            {
                FunctionsSeen.Add(request[7]);
            }

            if (Silent)
            {
                await Task.Delay(Timeout.Infinite, _stop.Token).ContinueWith(_ => { }, TaskScheduler.Default);
                return;
            }

            await stream.WriteAsync(Answer(request));
        }
    }

    private byte[] Answer(byte[] request)
    {
        byte function = request[7];
        ushort start = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(8));
        ushort count = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(10));

        byte[] pdu;

        if (AnswerWithException is byte code)
        {
            pdu = [(byte)(function | 0x80), code];
        }
        else if (function is 1 or 2)
        {
            int bytes = (count + 7) / 8;
            pdu = new byte[2 + bytes];
            pdu[0] = function;
            pdu[1] = (byte)bytes;
            for (int i = 0; i < count; i++)
            {
                if (Bits[start + i])
                {
                    pdu[2 + (i / 8)] |= (byte)(1 << (i % 8));
                }
            }
        }
        else
        {
            pdu = new byte[2 + (count * 2)];
            pdu[0] = function;
            pdu[1] = (byte)(count * 2);
            for (int i = 0; i < count; i++)
            {
                BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(2 + (i * 2)), Registers[start + i]);
            }
        }

        var frame = new byte[7 + pdu.Length];
        Array.Copy(request, frame, 4);   // transaction id and protocol id echoed
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), (ushort)(pdu.Length + 1));
        frame[6] = request[6];
        pdu.CopyTo(frame, 7);
        return frame;
    }
}
