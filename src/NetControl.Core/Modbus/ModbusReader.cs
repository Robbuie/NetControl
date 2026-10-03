using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using NetControl.Core.Reachability;

namespace NetControl.Core.Modbus;

/// <summary>
/// Reads coils, inputs and registers over Modbus/TCP. <b>Reads only.</b> There is no write method,
/// and the function enum it takes has no write in it.
///
/// <para>One connection per read, closed after it: the tool is asked a question and answers it,
/// rather than holding one of a small device's few Modbus connections open while somebody looks at
/// a table. One address, checked through <see cref="UnicastTarget"/> before the connect.</para>
/// </summary>
public sealed class ModbusReader
{
    private const int MaxFrame = 260;

    private ushort _transaction = 1;

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(3);

    public TimeSpan ResponseTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <exception cref="ReachabilityException">The address is not one host. Nothing was sent.</exception>
    /// <exception cref="ArgumentException">The request is outside what the protocol allows. Nothing was sent.</exception>
    /// <exception cref="ModbusException">No connection, or no answer.</exception>
    public async Task<ModbusReadResult> ReadAsync(ModbusReadRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        UnicastTarget.Ensure(request.Address);

        if (request.Problem() is { } problem)
        {
            throw new ArgumentException(problem, nameof(request));
        }

        ushort transaction = unchecked(_transaction++);
        long started = Stopwatch.GetTimestamp();

        using var client = new TcpClient(AddressFamily.InterNetwork);

        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            connect.CancelAfter(ConnectTimeout);

            try
            {
                await client.ConnectAsync(request.Address, request.Port, connect.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ModbusException($"Nothing answered on {request.Address}:{request.Port} within {ConnectTimeout.TotalSeconds:0} s.")
                {
                    Remediation = "Check the address, and that this PC has a route to it - Check services on the Diagnostics tab says whether 502 is open.",
                };
            }
            catch (SocketException ex)
            {
                throw new ModbusException($"{request.Address}:{request.Port} refused the connection ({ex.SocketErrorCode}).", ex)
                {
                    Remediation = ex.SocketErrorCode == SocketError.ConnectionRefused
                        ? "The device is there but is not serving Modbus/TCP on that port - check its port setting, or whether it allows only a fixed list of clients."
                        : "Check the address and the cable.",
                };
            }
        }

        using var reply = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        reply.CancelAfter(ResponseTimeout);

        var buffer = new byte[MaxFrame];
        int received = 0;

        try
        {
            NetworkStream stream = client.GetStream();
            await stream.WriteAsync(ModbusFrame.BuildRead(transaction, request), reply.Token).ConfigureAwait(false);

            while (received < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(received), reply.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                received += read;

                if (IsComplete(buffer, received))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (received == 0)
            {
                throw new ModbusException($"{request.Address} accepted the connection and did not answer the read within {ResponseTimeout.TotalSeconds:0} s.")
                {
                    Remediation = request.UnitId == 255
                        ? "Some devices ignore unit 255 - try unit 1."
                        : "A gateway stays silent for a unit id with nothing behind it. Check the unit id; on a device on Ethernet it is usually 1, sometimes 0 or 255.",
                };
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            if (received == 0)
            {
                throw new ModbusException($"{request.Address} closed the connection instead of answering.", ex)
                {
                    Remediation = "Many devices allow only a few Modbus connections at once, or only from listed addresses. Close other Modbus clients and try again.",
                };
            }
        }

        return Parse(buffer, received, transaction, request, Stopwatch.GetElapsedTime(started));
    }

    private static bool IsComplete(byte[] buffer, int received) =>
        ModbusFrame.ExpectedLength(buffer.AsSpan(0, received)) is int wanted && received >= wanted;

    private static ModbusReadResult Parse(byte[] buffer, int received, ushort transaction, ModbusReadRequest request, TimeSpan elapsed) =>
        ModbusFrame.Parse(buffer.AsSpan(0, received), transaction, request, elapsed);
}
